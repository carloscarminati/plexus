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
    private readonly AnthropicClient _client;

    public AnthropicTurnService(string apiKey)
    {
        _client = new AnthropicClient { ApiKey = apiKey };
    }

    // The model is chosen by the router (see ConversationService) and passed in.
    public async Task<TurnResult> CompleteAsync(TurnRequest request, string modelId, CancellationToken ct = default)
    {
        // Prompt-prefix caching (spec P1): cache the stable system prompt, and the
        // tail of the shared ancestor prefix. The last history entry is the new,
        // divergent user message — so the cache breakpoint goes on the entry before
        // it (Count-2). Sibling branches share that prefix and read it from cache.
        var history = request.History;
        var breakpoint = history.Count - 2; // -1 when there's nothing to cache yet

        var messages = new List<MessageParam>(history.Count);
        for (var i = 0; i < history.Count; i++)
        {
            var (role, content) = history[i];
            var roleEnum = role == "assistant" ? Role.Assistant : Role.User;
            messages.Add(i == breakpoint
                ? new MessageParam
                {
                    Role = roleEnum,
                    Content = new List<ContentBlockParam>
                    {
                        new TextBlockParam { Text = content, CacheControl = new CacheControlEphemeral() },
                    },
                }
                : new MessageParam { Role = roleEnum, Content = content });
        }

        var parameters = new MessageCreateParams
        {
            Model = modelId,
            MaxTokens = 16000,
            System = new List<TextBlockParam>
            {
                new() { Text = SystemPrompt.Text, CacheControl = new CacheControlEphemeral() },
            },
            Thinking = new ThinkingConfigAdaptive(),
            OutputConfig = new OutputConfig { Effort = Effort.High },
            Messages = messages,
        };

        var response = await _client.Messages.Create(parameters, cancellationToken: ct);

        if (response.Usage is { } usage)
        {
            Console.WriteLine(
                $"[plexus] tokens in={usage.InputTokens} out={usage.OutputTokens} " +
                $"cacheRead={usage.CacheReadInputTokens} cacheWrite={usage.CacheCreationInputTokens}");
        }

        var raw = string.Concat(
            response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));

        var blocks = ParseBlocks(raw);

        int? tokensIn = response.Usage is { } u ? (int)u.InputTokens : null;
        int? tokensOut = response.Usage is { } u2 ? (int)u2.OutputTokens : null;

        return new TurnResult(blocks, raw, tokensIn, tokensOut);
    }

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
