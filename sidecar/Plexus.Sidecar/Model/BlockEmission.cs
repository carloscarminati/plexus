using Plexus.Sidecar.Contract;

using Block = Plexus.Sidecar.Contract.Block;

namespace Plexus.Sidecar.Model;

// Strategy (a): the model is asked (via SystemPrompt) for a typed block array; we
// validate it against the catalog and parse. On any failure we fall back to (b),
// the heuristic parser, so a turn is always renderable. Provider-agnostic — used by
// the generic turn loop regardless of which IChatClient produced the text.
public static class BlockEmission
{
    public static List<Block> ParseBlocks(string raw)
    {
        var json = ExtractJsonObject(raw);
        if (json is not null && BlockCatalog.TryParse(json, out var blocks))
            return blocks;

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
}
