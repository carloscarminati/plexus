using Microsoft.Data.Sqlite;
using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Model;
using Plexus.Sidecar.Persistence;

namespace Plexus.Sidecar.Tests;

// ADR-0002 R2.2.0 — grounding (mock source, isolated). A retrieval-step pulls sources
// up front and adds them as source nodes; the facts step grounds each fact in a
// retrieved passage (cites its id), so provenance is VERIFIABLE (the ref resolves to a
// real source) — and the grounds edge (fact → source) is derived from source_ref, not
// stored. The mock corpus stands in for the real MCP catalog (R2.2.1).
public class ReasoningGroundingTests
{
    private static readonly IReadOnlyList<SourcePassage> Corpus = new SourcePassage[]
    {
        new("s1", "Source one.", FactSources.Doc),
        new("s2", "Source two.", FactSources.Api),
    };

    private static Recipe FrameFacts() => new()
    {
        Id = "t",
        Steps =
        {
            new() { Id = "frame", Role = ReasoningRoles.Frame, Prompt = "frame" },
            new() { Id = "facts", Role = ReasoningRoles.Fact, Array = true, MinItems = 1, Prompt = "facts" },
        },
    };

    private const string Frame = """{"question":"q"}""";

    // Grounded facts cite retrieved sources; the grounds edge is derived, and the source
    // kind comes authoritatively from the matched source (not the model's guess).
    [Fact]
    public async Task GroundedFacts_CiteRetrievedSources_AndDeriveGroundsEdges()
    {
        var client = new ScriptedChatClient(
            Frame,
            """{"facts":[{"claim":"A","sourceKind":"given","sourceRef":"s1"},{"claim":"B","sourceKind":"given","sourceRef":"s2"}]}""");

        var run = await RecipeExecutor.RunAsync(client, FrameFacts(), "small", factSource: new CuratedFactSource(Corpus));

        Assert.True(run.Ok);
        Assert.Equal(2, run.Graph.Nodes.Count(n => n.Reasoning?.Role == ReasoningRoles.Source)); // s1, s2 retrieved
        var facts = run.Graph.Nodes.Where(n => n.Reasoning?.Role == ReasoningRoles.Fact).ToList();
        Assert.All(facts, f => Assert.Contains(f.Reasoning!.SourceRef, new[] { "s1", "s2" }));
        // source kind is authoritative from the source node (s1=doc, s2=api), overriding "given".
        Assert.Equal(FactSources.Doc, facts.Single(f => f.Reasoning!.SourceRef == "s1").Reasoning!.SourceKind);
        Assert.Equal(FactSources.Api, facts.Single(f => f.Reasoning!.SourceRef == "s2").Reasoning!.SourceKind);
        // grounds edges fact → source, derived from source_ref.
        Assert.Equal(2, run.Graph.Edges.Count(e => e.Kind == ReasoningEdges.Grounds));
        Assert.Contains(run.Graph.Edges, e => e.Kind == ReasoningEdges.Grounds && e.To == "s1");
    }

    // A fact citing a NON-retrieved source is re-prompted (verifiable provenance), then
    // grounded — counted as a post-structural (referential) retry, never silently kept.
    [Fact]
    public async Task FactCitingUnretrievedSource_IsReprompted_ThenGrounded()
    {
        var client = new ScriptedChatClient(
            Frame,
            """{"facts":[{"claim":"A","sourceKind":"doc","sourceRef":"fabricated"}]}""", // not in the corpus
            """{"facts":[{"claim":"A","sourceKind":"doc","sourceRef":"s1"}]}""");

        var run = await RecipeExecutor.RunAsync(client, FrameFacts(), "small", factSource: new CuratedFactSource(Corpus));

        Assert.True(run.Ok);
        var facts = Assert.Single(run.Steps!, s => s.StepId == "facts");
        Assert.Equal(1, facts.ResolutionRetries); // a mis-citation, attributed to resolution
        Assert.Equal(0, facts.FidelityRetries);   // not fidelity (resolution gates it)
        Assert.Equal(0, facts.StructuralFailures);
        Assert.Single(run.Graph.Edges, e => e.Kind == ReasoningEdges.Grounds && e.To == "s1");
    }

    // Without a fact source, nothing is grounded — no source nodes, no grounds edges, the
    // model's source_ref is kept unchecked (backward-compatible with the ungrounded run).
    [Fact]
    public async Task NoFactSource_IsUngrounded_BackwardCompatible()
    {
        var client = new ScriptedChatClient(
            Frame,
            """{"facts":[{"claim":"A","sourceKind":"doc","sourceRef":"whatever"}]}""");

        var run = await RecipeExecutor.RunAsync(client, FrameFacts(), "small");

        Assert.True(run.Ok);
        Assert.DoesNotContain(run.Graph.Nodes, n => n.Reasoning?.Role == ReasoningRoles.Source);
        Assert.DoesNotContain(run.Graph.Edges, e => e.Kind == ReasoningEdges.Grounds);
        Assert.Equal("whatever", run.Graph.Nodes.Single(n => n.Reasoning?.Role == ReasoningRoles.Fact).Reasoning!.SourceRef);
    }

    // A grounded graph round-trips: source nodes persist as nodes; grounds is NOT stored
    // but re-derived from source_ref on load — so the reloaded graph carries the same
    // grounds edges, and R1 sees identical diagnostics.
    [Fact]
    public async Task GroundedGraph_RoundTrips_GroundsRederivedNotStored()
    {
        var run = await RecipeExecutor.RunAsync(
            new ScriptedChatClient(Frame, """{"facts":[{"claim":"A","sourceKind":"given","sourceRef":"s1"}]}"""),
            FrameFacts(), "small", factSource: new CuratedFactSource(Corpus));

        using var fx = new TempStore();
        var g = fx.Store.CreateGraph(null);
        foreach (var node in run.Graph.Nodes)
            fx.Store.AddNode(g.Id, node);
        fx.Store.SaveEdges(g.Id, run.Graph.Edges);

        var reloaded = fx.Store.LoadGraph(g.Id)!;

        Assert.Contains(reloaded.Nodes, n => n.Reasoning?.Role == ReasoningRoles.Source && n.Id == "s1");
        var grounds = Assert.Single(reloaded.Edges, e => e.Kind == ReasoningEdges.Grounds);
        Assert.Equal("s1", grounds.To);

        var before = ReasoningGraphValidator.Validate(run.Graph);
        var after = ReasoningGraphValidator.Validate(reloaded);
        Assert.Equal(before.HasErrors, after.HasErrors);
        Assert.Equal(before.Diagnostics.Count, after.Diagnostics.Count);
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
