using Microsoft.Data.Sqlite;
using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Persistence;
using Plexus.Sidecar.Services;

namespace Plexus.Sidecar.Tests;

// ADR-0002 R0 — reasoning metadata is additive and behavior-neutral. These tests
// prove (a) a node/edge carrying reasoning metadata round-trips losslessly, and
// (b) a graph with NO reasoning metadata round-trips identically to before (the
// new fields stay absent in JSON and persist as NULL → load back as null).
public class ReasoningContractTests
{
    // (a) JSON round-trip: a reasoning fact node + a weighted supports edge survive
    // serialize → deserialize with every field intact.
    [Fact]
    public void ReasoningNodeAndEdge_JsonRoundTrips_Lossless()
    {
        var node = new Node
        {
            Id = "f1",
            Role = "assistant",
            CreatedAt = "2026-06-09T00:00:00Z",
            Reasoning = new ReasoningMeta
            {
                Role = ReasoningRoles.Fact,
                SourceKind = FactSources.Doc,
                SourceRef = "control-catalog://bowtie/42",
            },
        };
        var edge = new Edge { From = "f1", To = "h1", Kind = ReasoningEdges.Supports, Weight = 0.75 };

        var node2 = PlexusJson.Deserialize<Node>(PlexusJson.Serialize(node))!;
        var edge2 = PlexusJson.Deserialize<Edge>(PlexusJson.Serialize(edge))!;

        Assert.Equal(ReasoningRoles.Fact, node2.Reasoning!.Role);
        Assert.Equal(FactSources.Doc, node2.Reasoning.SourceKind);
        Assert.Equal("control-catalog://bowtie/42", node2.Reasoning.SourceRef);
        Assert.Equal(ReasoningEdges.Supports, edge2.Kind);
        Assert.Equal(0.75, edge2.Weight);
    }

    // (b) Backward-compat: a legacy node/edge (no reasoning metadata) emits NO new
    // keys (omit-null) and loads back with the fields null — byte-identical contract.
    [Fact]
    public void LegacyNodeAndEdge_OmitReasoningKeys_AndRoundTripUnchanged()
    {
        var node = new Node { Id = "u1", Role = "user", CreatedAt = "2026-06-09T00:00:00Z" };
        var edge = new Edge { From = "u1", To = "a1" };

        var nodeJson = PlexusJson.Serialize(node);
        var edgeJson = PlexusJson.Serialize(edge);

        Assert.DoesNotContain("reasoning", nodeJson);
        Assert.DoesNotContain("kind", edgeJson);
        Assert.DoesNotContain("weight", edgeJson);

        Assert.Null(PlexusJson.Deserialize<Node>(nodeJson)!.Reasoning);
        var edge2 = PlexusJson.Deserialize<Edge>(edgeJson)!;
        Assert.Null(edge2.Kind);
        Assert.Null(edge2.Weight);
    }

    // (a) Persistence round-trip: node reasoning metadata survives a SQLite save/load.
    [Fact]
    public void ReasoningNode_PersistenceRoundTrips()
    {
        using var fx = new TempStore();
        var g = fx.Store.CreateGraph(null);
        fx.Store.AddNode(g.Id, new Node
        {
            Id = "f1",
            Role = "assistant",
            CreatedAt = "2026-06-09T00:00:00Z",
            Reasoning = new ReasoningMeta { Role = ReasoningRoles.Fact, SourceKind = FactSources.Api, SourceRef = "api://ledger/7" },
        });

        var reloaded = fx.Store.LoadGraph(g.Id)!.Nodes.Single();

        Assert.Equal(ReasoningRoles.Fact, reloaded.Reasoning!.Role);
        Assert.Equal(FactSources.Api, reloaded.Reasoning.SourceKind);
        Assert.Equal("api://ledger/7", reloaded.Reasoning.SourceRef);
    }

    // (b) Backward-compat: a legacy node (no reasoning) persists + reloads with
    // Reasoning == null — identical to pre-R0 behavior.
    [Fact]
    public void LegacyNode_PersistsWithNullReasoning()
    {
        using var fx = new TempStore();
        var g = fx.Store.CreateGraph(null);
        fx.Store.AddNode(g.Id, new Node { Id = "u1", Role = "user", CreatedAt = "2026-06-09T00:00:00Z" });

        var reloaded = fx.Store.LoadGraph(g.Id)!.Nodes.Single();

        Assert.Null(reloaded.Reasoning);
    }

    // Temp-SQLite fixture (mirrors GraphStoreTests' disposal).
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
