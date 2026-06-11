using Microsoft.Extensions.AI;
using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Persistence;

namespace Plexus.Sidecar.Model;

// ADR-0002 Rx (walking skeleton) — runs a recipe and persists the reasoning graph via
// the app's real persistence path (the same CreateGraph + AddNode + SaveEdges any node/
// edge uses). This is a new CALLER of the engine, not new engine behavior: it invokes
// RecipeExecutor unchanged. The chat client is injected (Func<IChatClient>) so a dev
// trigger uses the real provider and tests use a stub. Grounding is the mock source.
public sealed class RecipeRunner
{
    private const string Model = "claude-haiku-4-5";
    private readonly Func<IChatClient> _makeClient;
    private readonly GraphStore _store;

    public RecipeRunner(Func<IChatClient> makeClient, GraphStore store)
    {
        _makeClient = makeClient;
        _store = store;
    }

    // Run `recipeId` (default: investigator) over raw case text, persist the graph, and
    // return its id. Validation throws BEFORE any graph is created, so a malformed request
    // leaves no partial graph.
    public async Task<string> RunAndPersistAsync(string? recipeId, string caseText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(caseText))
            throw new ArgumentException("caseText is required.");

        var recipe = (recipeId ?? "") switch
        {
            "" or "investigator" => Recipes.Investigator,
            _ => throw new ArgumentException($"unknown recipe '{recipeId}'."),
        };

        // Run the recipe to completion IN MEMORY first. A step that fails (e.g. fidelity's
        // explicit-fail) returns Ok=false here — before any persistence — so the run never
        // reaches the commit below.
        using var client = _makeClient();
        var run = await RecipeExecutor.RunAsync(client, recipe, Model, context: caseText, factSource: new CuratedFactSource(), ct: ct);
        if (!run.Ok)
            throw new InvalidOperationException(run.Error ?? "recipe run failed");

        // Only now, on a successful run, persist ATOMICALLY (all-or-nothing) via the real
        // path. A db failure mid-write rolls back — no partial/orphaned graph.
        return _store.PersistGraph(run.Graph, Title(caseText));
    }

    private static string Title(string caseText)
    {
        var line = caseText.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "Investigation";
        return line.Length <= 60 ? line : line[..60] + "…";
    }
}
