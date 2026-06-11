using System.Text.Json;
using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Services;

namespace Plexus.Sidecar.Mcp;

// ADR-0002 R2.2.1 — the real grounding source: an IFactSource backed by an MCP tool (the
// control/bowtie catalog + operational APIs as MCP servers). Swaps in for the curated
// mock (R2.2.0) behind the same interface; the recipe executor is unchanged.
//
// Catalog tool contract: given { query: <case text> }, the tool returns a JSON array of
// passages — [{ "id": "...", "text": "...", "kind": "doc|api|given" }]. The id is
// load-bearing (it becomes the fact's source_ref, the source node id, and derives the
// grounds edge), so the catalog must return STABLE ids per passage. `kind` defaults to
// "doc" if absent. A tool error / unparseable result yields no sources (the run then
// proceeds ungrounded — surfaced by zero source nodes, never a silent invented ref).
public sealed class McpFactSource : IFactSource
{
    private readonly Func<string, CancellationToken, Task<string>> _retrieve;

    // Test seam: inject the retrieval call directly.
    public McpFactSource(Func<string, CancellationToken, Task<string>> retrieve) => _retrieve = retrieve;

    // Production: wire to an MCP catalog tool. The case text is passed as the query arg.
    public McpFactSource(McpHost mcp, string serverId, string toolName, string queryArg = "query")
        : this((q, ct) => mcp.CallAsync(serverId, toolName,
            new Dictionary<string, JsonElement> { [queryArg] = JsonSerializer.SerializeToElement(q) }, ct))
    {
    }

    public async Task<IReadOnlyList<SourcePassage>> RetrieveAsync(string caseText, CancellationToken ct = default)
    {
        var raw = await _retrieve(caseText, ct);
        if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith("[error]", StringComparison.Ordinal) || raw.StartsWith("[tool error]", StringComparison.Ordinal))
            return Array.Empty<SourcePassage>();

        List<PassageDto>? dtos;
        try { dtos = PlexusJson.Deserialize<List<PassageDto>>(raw); }
        catch (JsonException) { return Array.Empty<SourcePassage>(); }
        if (dtos is null)
            return Array.Empty<SourcePassage>();

        // Keep only well-formed passages (a stable id + text); kind defaults to doc.
        return dtos
            .Where(d => !string.IsNullOrWhiteSpace(d.Id) && !string.IsNullOrWhiteSpace(d.Text))
            .Select(d => new SourcePassage(d.Id!, d.Text!, string.IsNullOrWhiteSpace(d.Kind) ? FactSources.Doc : d.Kind!))
            .ToList();
    }

    private sealed record PassageDto(string? Id, string? Text, string? Kind);
}
