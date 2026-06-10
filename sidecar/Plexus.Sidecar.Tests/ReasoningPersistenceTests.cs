using Microsoft.Data.Sqlite;
using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Model;
using Plexus.Sidecar.Persistence;

namespace Plexus.Sidecar.Tests;

// ADR-0002 R2.1 — relational-edge persistence. The gate (from the ADR, not a footnote):
// a reasoning graph saved → reloaded must be byte-identical in what R1 sees — semantic
// edges (with weights) loaded from storage, structural edges re-derived from parentId,
// grounds derived from source_ref — so ReasoningGraphValidator yields the SAME
// diagnostics before and after. A run that produced a real producer graph is the input.
public class ReasoningPersistenceTests
{
    // A scripted investigator run whose conclusion selects a NET-NEGATIVE hypothesis →
    // R1 raises a flag. The flag depends on the supports/refutes WEIGHTS, so it only
    // survives the round-trip if the weights persisted: a lost weight zeros the net and
    // the flag vanishes → the assertion fails. That's the weight round-trip guard.
    private static readonly string[] NetNegativeRun =
    {
        """{"question":"q"}""",
        """{"facts":[{"claim":"A","sourceKind":"doc","sourceRef":"r1"},{"claim":"B","sourceKind":"api","sourceRef":"r2"}]}""",
        """{"uncertainties":[{"question":"u?"}]}""",
        """{"hypotheses":[{"statement":"H0","addresses":["u0"]},{"statement":"H1","addresses":["u0"]}]}""",
        """{"weighings":[{"fact":"f0","hypothesis":"h0","stance":"refutes","weight":0.9},{"fact":"f1","hypothesis":"h0","stance":"supports","weight":0.2}]}""",
        """{"selects":"h0","cites":["f0"]}""",
    };

    [Fact]
    public async Task ReasoningGraph_RoundTrips_WithIdenticalR1Diagnostics()
    {
        var run = await RecipeExecutor.RunAsync(new ScriptedChatClient(NetNegativeRun), Recipes.Investigator, "test-model");
        Assert.True(run.Ok);

        var before = ReasoningGraphValidator.Validate(run.Graph);
        Assert.True(before.HasFlags); // the test is meaningful: there IS a diagnostic to preserve

        using var fx = new TempStore();
        var g = fx.Store.CreateGraph(null);
        foreach (var node in run.Graph.Nodes)
            fx.Store.AddNode(g.Id, node);
        fx.Store.SaveEdges(g.Id, run.Graph.Edges);

        var reloaded = fx.Store.LoadGraph(g.Id)!;
        var after = ReasoningGraphValidator.Validate(reloaded);

        // The semantic round-trip bar: R1 sees the same thing.
        Assert.Equal(before.HasErrors, after.HasErrors);
        Assert.Equal(before.HasFlags, after.HasFlags);
        Assert.Equal(Key(before), Key(after));
        Assert.Equal(before.OpenUncertainties.OrderBy(x => x), after.OpenUncertainties.OrderBy(x => x));

        // And the weighted edges themselves survived with their magnitudes.
        var refutes = Assert.Single(reloaded.Edges, e => e.Kind == ReasoningEdges.Refutes);
        Assert.Equal(0.9, refutes.Weight);
        Assert.Single(reloaded.Edges, e => e.Kind == ReasoningEdges.Supports && e.Weight == 0.2);

        static IEnumerable<string> Key(ReasoningValidationResult r) =>
            r.Diagnostics.Select(d => $"{d.Severity}:{d.Code}:{d.NodeId}").OrderBy(x => x);
    }

    // Structural edges are NOT stored (they derive from parentId); only semantic edges
    // land in the table. A graph with no semantic edges stores none.
    [Fact]
    public void StructuralEdges_AreNotPersisted()
    {
        using var fx = new TempStore();
        var g = fx.Store.CreateGraph(null);
        fx.Store.AddNode(g.Id, new Node { Id = "a", Role = "user", CreatedAt = "0" });
        fx.Store.AddNode(g.Id, new Node { Id = "b", ParentId = "a", Role = "assistant", CreatedAt = "1" });
        fx.Store.SaveEdges(g.Id, new[] { new Edge { From = "a", To = "b" } }); // structural, kind == null

        var reloaded = fx.Store.LoadGraph(g.Id)!;

        // The a→b edge comes back derived from parentId, and carries no kind.
        var edge = Assert.Single(reloaded.Edges);
        Assert.Null(edge.Kind);
    }

    private sealed class TempStore : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"plexus-test-{Guid.NewGuid():n}.sqlite");
        public GraphStore Store { get; }
        public TempStore() => Store = new GraphStore(_path);
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch { /* best-effort */ }
        }
    }
}
