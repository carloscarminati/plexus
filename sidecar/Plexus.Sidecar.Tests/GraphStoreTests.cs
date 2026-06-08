using Microsoft.Data.Sqlite;
using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Persistence;

namespace Plexus.Sidecar.Tests;

// Conversation-list management: rename persistence + authority, delete cascade
// (no orphans), and the empty-conversation prune predicate (the key safety control:
// a conversation with any real turn is NEVER pruned). Runs against a temp SQLite db.
public class GraphStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly GraphStore _store;

    public GraphStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"plexus-test-{Guid.NewGuid():n}.sqlite");
        _store = new GraphStore(_dbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { if (File.Exists(f)) File.Delete(f); } catch { /* best-effort */ }
    }

    private static Node TextNode(string id, string text) => new()
    {
        Id = id,
        Role = "user",
        CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
        Raw = text,
        Blocks = new List<Block> { new MarkdownBlock { Text = text } },
    };

    private int NodeCount(string graphId)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM nodes WHERE graph_id = $g;";
        cmd.Parameters.AddWithValue("$g", graphId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [Fact]
    public void Rename_persists_across_reload_and_does_not_affect_other_conversations()
    {
        var a = _store.CreateGraph(null);
        var b = _store.CreateGraph(null);

        _store.SetGraphTitle(a.Id, "My decision brief");

        // Reload from a fresh store on the same file → persisted.
        var reloaded = new GraphStore(_dbPath);
        Assert.Equal("My decision brief", reloaded.LoadGraph(a.Id)!.Title);
        Assert.Null(reloaded.LoadGraph(b.Id)!.Title); // B untouched
    }

    [Fact]
    public void Delete_cascades_nodes_with_no_orphans_and_leaves_other_conversations_intact()
    {
        var a = _store.CreateGraph(null);
        var b = _store.CreateGraph(null);
        _store.AddNode(a.Id, TextNode("a1", "question"));
        _store.AddNode(a.Id, TextNode("a2", "answer"));
        _store.AddNode(b.Id, TextNode("b1", "other"));

        _store.DeleteGraph(a.Id);

        Assert.Null(_store.LoadGraph(a.Id));   // graph gone
        Assert.Equal(0, NodeCount(a.Id));      // its nodes cascaded — no orphans
        Assert.NotNull(_store.LoadGraph(b.Id)); // B intact
        Assert.Equal(1, NodeCount(b.Id));
    }

    [Fact]
    public void Prune_deletes_empties_but_never_a_conversation_with_a_turn()
    {
        var empty = _store.CreateGraph(null);
        var real = _store.CreateGraph(null);
        _store.AddNode(real.Id, TextNode("n1", "a real turn"));

        Assert.True(_store.IsEmptyGraph(empty.Id));
        Assert.False(_store.IsEmptyGraph(real.Id)); // KEY safety control

        var pruned = _store.PruneEmptyGraphs(exceptId: null);

        Assert.Equal(1, pruned);
        Assert.Null(_store.LoadGraph(empty.Id));    // empty pruned
        Assert.NotNull(_store.LoadGraph(real.Id));  // non-empty preserved
        Assert.Equal(1, NodeCount(real.Id));
    }

    [Fact]
    public void Pin_toggle_persists_and_is_included_in_the_list_payload()
    {
        var a = _store.CreateGraph(null);
        var b = _store.CreateGraph(null); // newer → would sort first when unpinned

        Assert.False(_store.ListGraphs().Single(g => g.Id == a.Id).Pinned); // default unpinned

        _store.SetGraphPinned(a.Id, true);

        // Reflected in the list payload; B unaffected.
        Assert.True(_store.ListGraphs().Single(g => g.Id == a.Id).Pinned);
        Assert.False(_store.ListGraphs().Single(g => g.Id == b.Id).Pinned);

        // Persists across reload, and pinned sorts to the top.
        var list = new GraphStore(_dbPath).ListGraphs();
        Assert.True(list.Single(g => g.Id == a.Id).Pinned);
        Assert.Equal(a.Id, list[0].Id);

        // Toggle back off.
        _store.SetGraphPinned(a.Id, false);
        Assert.False(new GraphStore(_dbPath).ListGraphs().Single(g => g.Id == a.Id).Pinned);
    }

    [Fact]
    public void Prune_and_DeleteIfEmpty_never_remove_the_active_or_a_non_empty_graph()
    {
        var active = _store.CreateGraph(null); // empty but active
        var other = _store.CreateGraph(null);  // empty, not active
        var real = _store.CreateGraph(null);
        _store.AddNode(real.Id, TextNode("n1", "turn"));

        // Active excepted; only the non-active empty is pruned (real is non-empty).
        var pruned = _store.PruneEmptyGraphs(exceptId: active.Id);
        Assert.Equal(1, pruned);
        Assert.NotNull(_store.LoadGraph(active.Id)); // active kept
        Assert.Null(_store.LoadGraph(other.Id));     // non-active empty pruned
        Assert.NotNull(_store.LoadGraph(real.Id));

        // DeleteIfEmpty respects the predicate: no-op on a graph with a turn.
        Assert.False(_store.DeleteIfEmpty(real.Id));
        Assert.NotNull(_store.LoadGraph(real.Id));
        Assert.True(_store.DeleteIfEmpty(active.Id)); // empty → deletable
        Assert.Null(_store.LoadGraph(active.Id));
    }
}
