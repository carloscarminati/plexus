using System.Text;
using Microsoft.Extensions.AI;
using Plexus.Sidecar.Contract;

using Block = Plexus.Sidecar.Contract.Block;
using AnthropicMessages = Anthropic.Models.Messages;

namespace Plexus.Sidecar.Model;

public sealed record TurnRequest(IReadOnlyList<(string Role, string Content)> History);

public sealed record TurnResult(List<Block> Blocks, string Raw, int? TokensIn, int? TokensOut);

// Provider-generic turn + tool-use loop (#1). Driven entirely through the
// Microsoft.Extensions.AI abstractions (IChatClient, ChatOptions.Tools,
// FunctionCallContent / FunctionResultContent) — NOT Anthropic SDK types — so the
// same code runs on Anthropic and any OpenAI-compatible backend. The M0 human gate
// lives in the injected executor (read-only auto-run, others confirm) and is
// unchanged. MCP tools are already AIFunctions, so they pass straight in.
public sealed class ChatTurnService
{
    private const int MaxToolRounds = 8; // safety cap on tool-use rounds per turn

    // Runs one tool call AFTER the M0 gate (the gate + the host call live in the
    // executor, provided by ConversationService). Returns the tool result text.
    public delegate Task<string> ToolExecutor(string callId, string toolName, IReadOnlyDictionary<string, object?> args, CancellationToken ct);

    public async Task<TurnResult> CompleteAsync(
        IChatClient client,
        string modelId,
        TurnRequest request,
        IReadOnlyList<AITool>? tools = null,
        ToolExecutor? executeTool = null,
        CancellationToken ct = default)
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, new AIContent[] { SystemContent() }) };
        foreach (var (role, content) in request.History)
            messages.Add(new ChatMessage(role == "assistant" ? ChatRole.Assistant : ChatRole.User, content));

        var hasTools = tools is { Count: > 0 } && executeTool is not null;

        // Adaptive thinking is Anthropic-only (opus 4.6+ / sonnet 4.6) — preserved
        // through the raw request seed; other providers ignore the seed. Prompt-prefix
        // caching is preserved separately (see SystemContent): the system prompt is the
        // large stable prefix, cached with one ephemeral breakpoint exactly as before.
        var advanced = modelId.Contains("opus", StringComparison.OrdinalIgnoreCase)
                       || modelId.Contains("sonnet-4-6", StringComparison.OrdinalIgnoreCase);

        var options = new ChatOptions
        {
            ModelId = modelId,
            MaxOutputTokens = 16000,
            Tools = hasTools ? tools!.ToList() : null,
            // One tool call per round (== Anthropic disable_parallel_tool_use): makes
            // the round cap a real bound and lets the gate confirm each call individually.
            AllowMultipleToolCalls = hasTools ? false : null,
            // The Anthropic adapter overwrites Model/MaxTokens/Messages/Tools from the
            // ChatOptions + message list; we seed only the fields the adapter doesn't
            // own (adaptive thinking + effort). Model/MaxTokens/Messages are set because
            // MessageCreateParams marks them `required` — their values here are ignored.
            RawRepresentationFactory = advanced
                ? _ => new AnthropicMessages.MessageCreateParams
                {
                    Model = modelId,
                    MaxTokens = 16000,
                    Messages = new List<AnthropicMessages.MessageParam>(),
                    Thinking = new AnthropicMessages.ThinkingConfigAdaptive(),
                    OutputConfig = new AnthropicMessages.OutputConfig { Effort = AnthropicMessages.Effort.High },
                }
                : null,
        };

        var transcript = new StringBuilder();
        int tokensIn = 0, tokensOut = 0, round = 0;
        var cappedOut = false;
        ChatResponse response;

        while (true)
        {
            response = await client.GetResponseAsync(messages, options, ct);
            if (response.Usage is { } u)
            {
                tokensIn += (int)(u.InputTokenCount ?? 0);
                tokensOut += (int)(u.OutputTokenCount ?? 0);
                // Parity diagnostic (was in AnthropicTurnService): surface cache read/write
                // so prompt-caching is observable. The adapter maps cache_read_input_tokens
                // to CachedInputTokenCount and cache_creation to AdditionalCounts.
                var cacheRead = u.CachedInputTokenCount ?? 0;
                long cacheWrite = u.AdditionalCounts?.TryGetValue("CacheCreationInputTokens", out var cw) == true ? cw : 0;
                Console.WriteLine($"[plexus] tokens in={u.InputTokenCount} out={u.OutputTokenCount} cacheRead={cacheRead} cacheWrite={cacheWrite}");
            }

            var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
            if (calls.Count == 0 || !hasTools)
            {
                messages.AddRange(response.Messages);
                break;
            }

            // Cap tool-use rounds per turn: stop cleanly (no error) at the limit.
            if (round >= MaxToolRounds)
            {
                cappedOut = true;
                transcript.AppendLine($"- (stopped: reached the {MaxToolRounds}-round tool-call limit)");
                messages.AddRange(response.Messages);
                break;
            }
            round++;

            // Echo the assistant turn (the adapter round-trips the provider's reasoning
            // so e.g. Anthropic thinking signatures are preserved across rounds).
            messages.AddRange(response.Messages);
            foreach (var call in calls)
            {
                var args = call.Arguments is { } a
                    ? (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(a)
                    : new Dictionary<string, object?>();
                var resultText = await executeTool!(call.CallId ?? "", call.Name, args, ct);
                transcript.AppendLine($"- {call.Name}(...) → {Truncate(resultText, 240)}");
                messages.Add(new ChatMessage(ChatRole.Tool, new AIContent[] { new FunctionResultContent(call.CallId ?? "", resultText) }));
            }
        }

        var text = response.Text ?? string.Empty;
        if (cappedOut && string.IsNullOrWhiteSpace(text))
            text = $"Stopped after reaching the {MaxToolRounds}-round tool-call limit for this turn.";

        var blocks = BlockEmission.ParseBlocks(text);
        // Keep `raw` faithful for resume (spec §4.4): include the tool transcript.
        var fullRaw = transcript.Length > 0 ? $"[tool calls]\n{transcript}[answer]\n{text}" : text;

        return new TurnResult(blocks, fullRaw, tokensIn, tokensOut);
    }

    // The system prompt is the large, stable prefix of every turn. We mark it with one
    // ephemeral cache breakpoint so Anthropic caches it across turns (M0 behavior). The
    // Anthropic adapter reads this from the content's AdditionalProperties under the
    // "anthropic:cache_control" key (AnthropicChatClient.GetCacheControl); providers that
    // don't understand the key (e.g. OpenAI-compatible) simply ignore it.
    internal static TextContent SystemContent() => new(SystemPrompt.Text)
    {
        AdditionalProperties = new AdditionalPropertiesDictionary
        {
            ["anthropic:cache_control"] = new AnthropicMessages.CacheControlEphemeral(),
        },
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
