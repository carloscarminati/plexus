using Microsoft.Data.Sqlite;
using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Model;
using Plexus.Sidecar.Persistence;

namespace Plexus.Sidecar.Tests;

// ADR-0002 Rx.2.0 — the human decision seam. An adjudication is additive review metadata:
// it round-trips with the graph, it NEVER mutates the reasoning (a flagged graph stays
// flagged after an accept), and a failed write leaves no partial state.
public class AdjudicationTests
{
    // A scripted investigator run whose conclusion selects a NET-NEGATIVE hypothesis → R1
    // flags it. Used to prove the flag survives adjudication.
    private static readonly string[] NetNegativeRun =
    {
        """{"question":"q"}""",
        """{"facts":[{"claim":"A","sourceKind":"doc","sourceRef":"r1"},{"claim":"B","sourceKind":"api","sourceRef":"r2"}]}""",
        """{"uncertainties":[{"question":"u?"}]}""",
        """{"hypotheses":[{"statement":"H0","addresses":["u0"]},{"statement":"H1","addresses":["u0"]}]}""",
        """{"weighings":[{"fact":"f0","hypothesis":"h0","stance":"refutes","weight":0.9},{"fact":"f1","hypothesis":"h0","stance":"supports","weight":0.2}]}""",
        """{"selects":"h0","cites":["f0"]}""",
    };

    // ── round-trip: a recorded adjudication reloads identically ───────────────
    [Fact]
    public void Adjudication_RoundTrips_WithAttributionAndTimestamp()
    {
        using var fx = new TempStore();
        var g = fx.Store.CreateGraph("t");

        var saved = fx.Store.SaveAdjudication(g.Id, AdjudicationDecisions.Accept, "looks sound", "carlos");
        var loaded = fx.Store.LoadAdjudication(g.Id)!;

        Assert.Equal(AdjudicationDecisions.Accept, loaded.Decision);
        Assert.Equal("looks sound", loaded.Note);
        Assert.Equal("carlos", loaded.Reviewer);
        Assert.Equal(saved.Timestamp, loaded.Timestamp);   // server-stamped, round-trips
        Assert.False(string.IsNullOrEmpty(loaded.Timestamp));
    }

    [Fact]
    public void Adjudication_IsUpdatable_OnePerGraph()
    {
        using var fx = new TempStore();
        var g = fx.Store.CreateGraph("t");

        fx.Store.SaveAdjudication(g.Id, AdjudicationDecisions.Accept, "first", "carlos");
        fx.Store.SaveAdjudication(g.Id, AdjudicationDecisions.Reject, "changed my mind", "carlos");

        var loaded = fx.Store.LoadAdjudication(g.Id)!;
        Assert.Equal(AdjudicationDecisions.Reject, loaded.Decision); // replaced, not duplicated
        Assert.Equal("changed my mind", loaded.Note);
    }

    [Fact]
    public void NoAdjudication_LoadsNull()
    {
        using var fx = new TempStore();
        var g = fx.Store.CreateGraph("t");
        Assert.Null(fx.Store.LoadAdjudication(g.Id));
    }

    // ── the integrity gate: accepting a net-negative graph does NOT clear the flag ──
    [Fact]
    public async Task AcceptingNetNegativeGraph_LeavesTheFlagIntact()
    {
        var run = await RecipeExecutor.RunAsync(new ScriptedChatClient(NetNegativeRun), Recipes.Investigator, "test-model");
        Assert.True(run.Ok);
        Assert.True(ReasoningGraphValidator.Validate(run.Graph).HasFlags); // there IS a flag to preserve

        using var fx = new TempStore();
        var g = fx.Store.CreateGraph(null);
        foreach (var node in run.Graph.Nodes)
            fx.Store.AddNode(g.Id, node);
        fx.Store.SaveEdges(g.Id, run.Graph.Edges);

        // The human accepts despite the flag, with a justification.
        fx.Store.SaveAdjudication(g.Id, AdjudicationDecisions.Accept, "accepted despite net-negative because X", "carlos");

        // Reload: the reasoning is UNTOUCHED — the flag is still there, beside the accept.
        var reloaded = fx.Store.LoadGraph(g.Id)!;
        var after = ReasoningGraphValidator.Validate(reloaded);
        Assert.True(after.HasFlags); // flagged AND accepted — both survive

        var adj = fx.Store.LoadAdjudication(g.Id)!;
        Assert.Equal(AdjudicationDecisions.Accept, adj.Decision);
        Assert.Equal("accepted despite net-negative because X", adj.Note);
    }

    // ── atomicity: a rejected write leaves the prior state intact, never partial ──
    [Fact]
    public void FailedWrite_RollsBack_PriorAdjudicationIntact()
    {
        using var fx = new TempStore();
        var g = fx.Store.CreateGraph("t");
        fx.Store.SaveAdjudication(g.Id, AdjudicationDecisions.Accept, "the good one", "carlos");

        // An invalid decision trips the DB CHECK constraint → the upsert throws and the
        // transaction rolls back. The prior adjudication must be exactly as it was.
        Assert.ThrowsAny<SqliteException>(() =>
            fx.Store.SaveAdjudication(g.Id, "maybe", "should never land", "carlos"));

        var loaded = fx.Store.LoadAdjudication(g.Id)!;
        Assert.Equal(AdjudicationDecisions.Accept, loaded.Decision); // untouched
        Assert.Equal("the good one", loaded.Note);
    }

    [Fact]
    public void FailedWrite_OnFreshGraph_WritesNothing()
    {
        using var fx = new TempStore();
        var g = fx.Store.CreateGraph("t");

        Assert.ThrowsAny<SqliteException>(() =>
            fx.Store.SaveAdjudication(g.Id, "garbage", null, "carlos"));

        Assert.Null(fx.Store.LoadAdjudication(g.Id)); // no partial row
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
