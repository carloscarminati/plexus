using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Services;

// `Block` exists in both our contract and the Anthropic SDK; disambiguate to ours.
using Block = Plexus.Sidecar.Contract.Block;

namespace Plexus.Sidecar.Model;

public sealed record TurnRequest(IReadOnlyList<(string Role, string Content)> History);

public sealed record TurnResult(List<Block> Blocks, string Raw, int? TokensIn, int? TokensOut);

// Strategy (a): ask the model for typed blocks via the system prompt, parse and
// validate the JSON. On any parse/validation failure we fall back to strategy
// (b), the heuristic parser, so a turn is always renderable.
//
// NOTE: we use prompt-guided JSON rather than strict structured outputs because
// the table `rows` map (arbitrary keys) can't be expressed under the API's
// strict-schema rules (which forbid open `additionalProperties`). A later
// hardening pass can move this to a constrained tool / structured output.
public sealed class AnthropicTurnService
{
    private const int MaxToolRounds = 8; // safety cap on tool-use rounds per turn
    private readonly Services.KeychainService _keychain;
    private AnthropicClient? _client;
    private string? _clientKey;

    // The key is resolved lazily (not captured at construction) so a key set from
    // Settings takes effect on the next turn without restarting the sidecar.
    public AnthropicTurnService(Services.KeychainService keychain)
    {
        _keychain = keychain;
    }

    private AnthropicClient Client()
    {
        var key = _keychain.GetAnthropicKey()
            ?? throw new InvalidOperationException("No Anthropic API key configured. Add it in Settings → Providers.");
        if (_client is null || _clientKey != key)
        {
            _client = new AnthropicClient { ApiKey = key };
            _clientKey = key;
        }
        return _client;
    }

    // Executes one tool call (the host call + the human gate live in the executor,
    // provided by ConversationService). Returns the tool result text fed back to
    // the model.
    public delegate Task<string> ToolExecutor(string toolUseId, string toolName, JsonElement args, CancellationToken ct);

    // The model is chosen by the router (see ConversationService) and passed in.
    // When `tools` is non-empty this runs an agentic loop: model → tool_use →
    // executor (gated) → tool_result → model, until the model stops calling tools.
    public async Task<TurnResult> CompleteAsync(
        TurnRequest request,
        string modelId,
        IReadOnlyList<Tool>? tools = null,
        ToolExecutor? executeTool = null,
        CancellationToken ct = default)
    {
        // Prompt-prefix caching (spec P1): cache the stable system prompt and the
        // tail of the shared ancestor prefix (the entry before the new user turn).
        var history = request.History;
        var breakpoint = history.Count - 2;
        var messages = new List<MessageParam>(history.Count);
        for (var i = 0; i < history.Count; i++)
        {
            var (role, content) = history[i];
            var roleEnum = role == "assistant" ? Role.Assistant : Role.User;
            messages.Add(i == breakpoint
                ? new MessageParam { Role = roleEnum, Content = new List<ContentBlockParam> { new TextBlockParam { Text = content, CacheControl = new CacheControlEphemeral() } } }
                : new MessageParam { Role = roleEnum, Content = content });
        }

        // Adaptive thinking + effort: Opus 4.6+ / Sonnet 4.6 only (400 on Haiku 4.5).
        var advanced = modelId.Contains("opus", StringComparison.OrdinalIgnoreCase)
                       || modelId.Contains("sonnet-4-6", StringComparison.OrdinalIgnoreCase);
        var toolUnions = tools is { Count: > 0 } ? tools.Select(t => (ToolUnion)t).ToList() : null;

        var transcript = new StringBuilder();
        int tokensIn = 0, tokensOut = 0;
        var round = 0;
        var cappedOut = false;
        Message response;

        while (true)
        {
            var parameters = BuildMessageParams(modelId, messages, advanced, toolUnions);
            response = await Client().Messages.Create(parameters, cancellationToken: ct);
            if (response.Usage is { } u)
            {
                tokensIn += (int)u.InputTokens;
                tokensOut += (int)u.OutputTokens;
                Console.WriteLine($"[plexus] tokens in={u.InputTokens} out={u.OutputTokens} cacheRead={u.CacheReadInputTokens} cacheWrite={u.CacheCreationInputTokens}");
            }

            if (response.StopReason != "tool_use" || toolUnions is null || executeTool is null)
                break;

            // Guard: cap tool-use rounds per turn. On the cap we stop cleanly (no
            // error) and return whatever the model has produced so far.
            if (round >= MaxToolRounds)
            {
                cappedOut = true;
                transcript.AppendLine($"- (stopped: reached the {MaxToolRounds}-round tool-call limit)");
                break;
            }
            round++;

            // Echo the assistant turn (preserving thinking signatures) + run each tool.
            var assistantContent = new List<ContentBlockParam>();
            var toolResults = new List<ContentBlockParam>();
            foreach (var block in response.Content)
            {
                if (block.TryPickText(out var tb))
                    assistantContent.Add(new TextBlockParam { Text = tb.Text });
                else if (block.TryPickThinking(out var th))
                    assistantContent.Add(new ThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
                else if (block.TryPickRedactedThinking(out var rt))
                    assistantContent.Add(new RedactedThinkingBlockParam { Data = rt.Data });
                else if (block.TryPickToolUse(out var tu))
                {
                    assistantContent.Add(new ToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
                    var argsJson = JsonSerializer.SerializeToElement(tu.Input);
                    var resultText = await executeTool(tu.ID, tu.Name, argsJson, ct);
                    transcript.AppendLine($"- {tu.Name}({argsJson.GetRawText()}) → {Truncate(resultText, 240)}");
                    toolResults.Add(new ToolResultBlockParam { ToolUseID = tu.ID, Content = resultText });
                }
            }
            messages.Add(new MessageParam { Role = Role.Assistant, Content = assistantContent });
            messages.Add(new MessageParam { Role = Role.User, Content = toolResults });
        }

        var raw = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        if (cappedOut && string.IsNullOrWhiteSpace(raw))
            raw = $"Stopped after reaching the {MaxToolRounds}-round tool-call limit for this turn.";
        var blocks = ParseBlocks(raw);

        // Keep `raw` faithful for resume (spec §4.4): include the tool transcript so a
        // resumed branch replays what tools were called and what they returned.
        var fullRaw = transcript.Length > 0 ? $"[tool calls]\n{transcript}[answer]\n{raw}" : raw;

        return new TurnResult(blocks, fullRaw, tokensIn, tokensOut);
    }

    // Builds the per-round request. Tool fields are set ONLY when tools exist.
    //
    // Subtle regression guard (see AnthropicTurnServiceTests): `ToolChoice` is a
    // union with an `implicit operator ToolChoice(ToolChoiceAuto)`. Writing the
    // tempting one-liner `ToolChoice = tools is not null ? new ToolChoiceAuto{…} : null`
    // makes the ternary's null flow THROUGH that implicit operator, producing a
    // *present* ToolChoice wrapping null → the body serializes `"tool_choice":null`
    // and the API 400s ("tool_choice: Input should be an object"). So for a tool-free
    // turn we must not set the property at all — hence the two construction branches.
    internal static MessageCreateParams BuildMessageParams(
        string modelId,
        List<MessageParam> messages,
        bool advanced,
        List<ToolUnion>? toolUnions)
    {
        var system = new List<TextBlockParam>
        {
            new() { Text = SystemPrompt.Text, CacheControl = new CacheControlEphemeral() },
        };
        var thinking = advanced ? new ThinkingConfigAdaptive() : (ThinkingConfigParam?)null;
        var outputConfig = advanced ? new OutputConfig { Effort = Effort.High } : null;

        return toolUnions is not null
            ? new MessageCreateParams
            {
                Model = modelId,
                MaxTokens = 16000,
                System = system,
                Thinking = thinking,
                OutputConfig = outputConfig,
                Messages = messages,
                Tools = toolUnions,
                // One tool call per round: makes the round cap a real bound and lets
                // the safety gate confirm each side-effecting call individually.
                ToolChoice = new ToolChoiceAuto { DisableParallelToolUse = true },
            }
            : new MessageCreateParams
            {
                Model = modelId,
                MaxTokens = 16000,
                System = system,
                Thinking = thinking,
                OutputConfig = outputConfig,
                Messages = messages,
            };
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    // Try strategy (a): parse the JSON {"blocks":[...]}. Fall back to (b).
    public static List<Block> ParseBlocks(string raw)
    {
        var json = ExtractJsonObject(raw);
        if (json is not null)
        {
            try
            {
                var doc = JsonSerializer.Deserialize<TurnEnvelope>(json, Json.Options);
                if (doc?.Blocks is { Count: > 0 } blocks)
                    return blocks;
            }
            catch (JsonException)
            {
                // fall through to the heuristic parser
            }
        }

        return FallbackParser.Parse(raw);
    }

    // The model is told to emit a bare JSON object, but be forgiving: strip a
    // ```json fence if one slipped in, otherwise take the outermost {...}.
    private static string? ExtractJsonObject(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
            return trimmed;

        var fenceStart = trimmed.IndexOf("```", StringComparison.Ordinal);
        if (fenceStart >= 0)
        {
            var afterFence = trimmed.IndexOf('\n', fenceStart);
            var fenceEnd = trimmed.IndexOf("```", fenceStart + 3, StringComparison.Ordinal);
            if (afterFence > 0 && fenceEnd > afterFence)
                return trimmed[(afterFence + 1)..fenceEnd].Trim();
        }

        var first = trimmed.IndexOf('{');
        var last = trimmed.LastIndexOf('}');
        if (first >= 0 && last > first)
            return trimmed[first..(last + 1)];

        return null;
    }

    private sealed class TurnEnvelope
    {
        public List<Block> Blocks { get; set; } = new();
    }
}
