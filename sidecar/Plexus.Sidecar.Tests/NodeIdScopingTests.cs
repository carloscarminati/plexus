using Microsoft.Data.Sqlite;
using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Persistence;

namespace Plexus.Sidecar.Tests;

// Node ids are GRAPH-scoped. Recipe reasoning nodes use local ids (n0, n1, …) that reset
// per run, so a second persisted recipe graph reuses them — under the old global `id`
// PRIMARY KEY that tripped "UNIQUE constraint failed: nodes.id". The key is now
// (graph_id, id); these pin that, plus the migration that rebuilds an old DB.
public class NodeIdScopingTests
{
    private static Graph OneFrame(string nodeId, string raw) =>
        new()
        {
            Nodes = new List<Node>
            {
                new()
                {
                    Id = nodeId,
                    Role = "assistant",
                    CreatedAt = "000000",
                    Blocks = new List<Block> { new MarkdownBlock { Text = raw } },
                    Raw = raw,
                    Reasoning = new ReasoningMeta { Role = ReasoningRoles.Frame },
                },
            },
        };

    // ── the bug repro: two recipe graphs with the SAME local node id both persist ──
    [Fact]
    public void TwoGraphs_WithSameLocalNodeId_BothPersistAndLoad()
    {
        using var fx = new TempDb();
        var store = new GraphStore(fx.Path);

        var id1 = store.PersistGraph(OneFrame("n0", "first"), "g1");
        var id2 = store.PersistGraph(OneFrame("n0", "second"), "g2"); // would throw under the global PK

        Assert.NotEqual(id1, id2);
        Assert.Equal("first", store.LoadGraph(id1)!.Nodes.Single(n => n.Id == "n0").Raw);
        Assert.Equal("second", store.LoadGraph(id2)!.Nodes.Single(n => n.Id == "n0").Raw);
    }

    // ── migration: an old global-PK DB is rebuilt, rows preserved, reuse unblocked ──
    [Fact]
    public void OldGlobalPkDb_Migrates_PreservingRows_AndAllowsLocalIdReuse()
    {
        using var fx = new TempDb();

        // Hand-build a DB with the OLD nodes schema (id TEXT PRIMARY KEY) + one row.
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fx.Path }.ToString()))
        {
            conn.Open();
            using var c = conn.CreateCommand();
            c.CommandText = """
                CREATE TABLE graphs (id TEXT PRIMARY KEY, title TEXT, created_at TEXT NOT NULL, policy_json TEXT, pinned INTEGER DEFAULT 0);
                CREATE TABLE nodes (id TEXT PRIMARY KEY, graph_id TEXT NOT NULL, parent_id TEXT, role TEXT NOT NULL, created_at TEXT NOT NULL, blocks_json TEXT NOT NULL, raw TEXT NOT NULL, meta_json TEXT, merge_parents_json TEXT, kind TEXT, reasoning_json TEXT);
                INSERT INTO graphs (id, title, created_at) VALUES ('g-old', 't', '2026-01-01');
                INSERT INTO nodes (id, graph_id, role, created_at, blocks_json, raw) VALUES ('n0', 'g-old', 'assistant', '000000', '[]', 'old node');
                """;
            c.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        // Opening through GraphStore runs the migration.
        var store = new GraphStore(fx.Path);

        // The old row survived the rebuild.
        Assert.Equal("old node", store.LoadGraph("g-old")!.Nodes.Single(n => n.Id == "n0").Raw);

        // And a NEW graph reusing the local id "n0" now persists (composite key active).
        var id2 = store.PersistGraph(OneFrame("n0", "fresh"), "g2");
        Assert.Equal("fresh", store.LoadGraph(id2)!.Nodes.Single(n => n.Id == "n0").Raw);
        Assert.Equal("old node", store.LoadGraph("g-old")!.Nodes.Single(n => n.Id == "n0").Raw); // untouched
    }

    private sealed class TempDb : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"plexus-test-{Guid.NewGuid():n}.sqlite");
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { Path, Path + "-wal", Path + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch { /* best-effort */ }
        }
    }
}
