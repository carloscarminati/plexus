using Microsoft.Data.Sqlite;
using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Routing;
using Plexus.Sidecar.Services;

namespace Plexus.Sidecar.Persistence;

// Local-first persistence. The graph lives on disk at ~/.plexus/plexus.sqlite
// and reloads on restart. Blocks and meta are stored as JSON columns; edges are
// derivable from parentId, so we only persist nodes.
public sealed class GraphStore
{
    private readonly string _connectionString;

    public GraphStore(string? dbPath = null)
    {
        var path = dbPath ?? DefaultDbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        Initialize();
    }

    public static string DefaultDbPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".plexus", "plexus.sqlite");
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS graphs (
                id          TEXT PRIMARY KEY,
                title       TEXT,
                created_at  TEXT NOT NULL,
                policy_json TEXT
            );
            CREATE TABLE IF NOT EXISTS nodes (
                id          TEXT PRIMARY KEY,
                graph_id    TEXT NOT NULL REFERENCES graphs(id) ON DELETE CASCADE,
                parent_id   TEXT,
                role        TEXT NOT NULL,
                created_at  TEXT NOT NULL,
                blocks_json TEXT NOT NULL,
                raw         TEXT NOT NULL,
                meta_json   TEXT,
                merge_parents_json TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_nodes_graph ON nodes(graph_id);
            -- Semantic reasoning edges (ADR-0002 R2.1). Structural branch/merge edges are
            -- still DERIVED from parentId on load (not stored); only typed edges with a
            -- kind live here — their from/to/kind/weight are not recoverable from any node
            -- field. `grounds` is excluded: it is derived from a fact's source_ref.
            CREATE TABLE IF NOT EXISTS edges (
                graph_id TEXT NOT NULL REFERENCES graphs(id) ON DELETE CASCADE,
                from_id  TEXT NOT NULL,
                to_id    TEXT NOT NULL,
                kind     TEXT NOT NULL,
                weight   REAL
            );
            CREATE INDEX IF NOT EXISTS idx_edges_graph ON edges(graph_id);
            -- Human adjudications (ADR-0002 Rx.2.0). A SEPARATE record from the reasoning
            -- nodes/edges — additive review metadata, one per graph (register/update), that
            -- travels with the graph on load. The CHECK is the atomicity backstop: a bad
            -- decision is rejected by the DB, so the upsert rolls back rather than writing
            -- a partial/garbage row.
            CREATE TABLE IF NOT EXISTS adjudications (
                graph_id  TEXT PRIMARY KEY REFERENCES graphs(id) ON DELETE CASCADE,
                decision  TEXT NOT NULL CHECK (decision IN ('accept', 'reject')),
                note      TEXT,
                reviewer  TEXT NOT NULL,
                timestamp TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        // Migrations for older DBs (ALTER throws if the column already exists).
        foreach (var (table, col) in new[] { ("graphs", "policy_json TEXT"), ("nodes", "merge_parents_json TEXT"), ("nodes", "kind TEXT"), ("graphs", "pinned INTEGER DEFAULT 0"), ("nodes", "reasoning_json TEXT") })
        {
            using var migrate = conn.CreateCommand();
            migrate.CommandText = $"ALTER TABLE {table} ADD COLUMN {col};";
            try { migrate.ExecuteNonQuery(); }
            catch (SqliteException) { /* column already exists */ }
        }
    }

    // Most-recently-active first. "Active" = latest node's createdAt (derived, no
    // write-path change), falling back to the graph's own createdAt when empty.
    public List<GraphSummary> ListGraphs()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT g.id, g.title, COALESCE(MAX(n.created_at), g.created_at) AS updated_at, COALESCE(g.pinned, 0)
            FROM graphs g LEFT JOIN nodes n ON n.graph_id = g.id
            GROUP BY g.id, g.title, g.created_at, g.pinned
            ORDER BY g.pinned DESC, updated_at DESC;
            """;
        using var reader = cmd.ExecuteReader();
        var result = new List<GraphSummary>();
        while (reader.Read())
        {
            result.Add(new GraphSummary
            {
                Id = reader.GetString(0),
                Title = reader.IsDBNull(1) ? null : reader.GetString(1),
                UpdatedAt = reader.IsDBNull(2) ? null : reader.GetString(2),
                Pinned = !reader.IsDBNull(3) && reader.GetInt64(3) != 0,
            });
        }
        return result;
    }

    public void SetGraphTitle(string graphId, string? title)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE graphs SET title = $title WHERE id = $id;";
        cmd.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", graphId);
        cmd.ExecuteNonQuery();
    }

    public void SetGraphPinned(string graphId, bool pinned)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE graphs SET pinned = $p WHERE id = $id;";
        cmd.Parameters.AddWithValue("$p", pinned ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", graphId);
        cmd.ExecuteNonQuery();
    }

    // Destructive: removes the graph and its nodes. (ON DELETE CASCADE is declared
    // but only enforced when foreign_keys is ON per-connection, so delete both
    // explicitly inside a transaction.)
    public void DeleteGraph(string graphId)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var ncmd = conn.CreateCommand())
        {
            ncmd.Transaction = tx;
            ncmd.CommandText = "DELETE FROM nodes WHERE graph_id = $id;";
            ncmd.Parameters.AddWithValue("$id", graphId);
            ncmd.ExecuteNonQuery();
        }
        using (var gcmd = conn.CreateCommand())
        {
            gcmd.Transaction = tx;
            gcmd.CommandText = "DELETE FROM graphs WHERE id = $id;";
            gcmd.Parameters.AddWithValue("$id", graphId);
            gcmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public bool GraphExists(string graphId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM graphs WHERE id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", graphId);
        return cmd.ExecuteScalar() is not null;
    }

    // A conversation is "empty" iff it has ZERO nodes. Nodes are only persisted when
    // a real turn runs (a user node + its assistant reply), so zero nodes is the
    // precise, conservative predicate — a graph with any node is never empty, and we
    // never prune on a fuzzier guess.
    public bool IsEmptyGraph(string graphId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT NOT EXISTS(SELECT 1 FROM nodes WHERE graph_id = $id);";
        cmd.Parameters.AddWithValue("$id", graphId);
        return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
    }

    // Delete `graphId` only if it is empty (zero nodes). Returns true if it was
    // deleted. Safe to call on a non-empty or non-existent id (no-op).
    public bool DeleteIfEmpty(string graphId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM graphs WHERE id = $id AND NOT EXISTS(SELECT 1 FROM nodes WHERE graph_id = $id);";
        cmd.Parameters.AddWithValue("$id", graphId);
        return cmd.ExecuteNonQuery() > 0;
    }

    // Delete every empty graph EXCEPT `exceptId` (the active conversation, never
    // pruned out from under the user). Empties have no nodes, so nothing is orphaned.
    // Returns the number pruned.
    public int PruneEmptyGraphs(string? exceptId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = exceptId is null
            ? "DELETE FROM graphs WHERE NOT EXISTS(SELECT 1 FROM nodes WHERE nodes.graph_id = graphs.id);"
            : "DELETE FROM graphs WHERE id != $except AND NOT EXISTS(SELECT 1 FROM nodes WHERE nodes.graph_id = graphs.id);";
        if (exceptId is not null)
            cmd.Parameters.AddWithValue("$except", exceptId);
        return cmd.ExecuteNonQuery();
    }

    public Graph CreateGraph(string? title)
    {
        var graph = new Graph
        {
            Id = Guid.NewGuid().ToString("n"),
            Title = title,
            // Default session policy: manual on the large model (preserves R0
            // behaviour); the user can switch to an auto policy in the UI.
            DefaultPolicy = RoutingPolicy.Manual("claude-opus-4-8"),
        };
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO graphs (id, title, created_at, policy_json) VALUES ($id, $title, $createdAt, $policy);";
        cmd.Parameters.AddWithValue("$id", graph.Id);
        cmd.Parameters.AddWithValue("$title", (object?)graph.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$policy", PlexusJson.Serialize(graph.DefaultPolicy));
        cmd.ExecuteNonQuery();
        return graph;
    }

    // Persist a freshly-produced graph ATOMICALLY (ADR-0002 Rx): the graph row, all its
    // nodes, and its semantic edges go in within ONE transaction — a db failure mid-write
    // rolls back to nothing, never a partial/orphaned graph. (The caller runs the recipe to
    // completion in memory first; this is the all-or-nothing commit.) Structural and grounds
    // edges are not stored (derived on load), matching the R2.1/R2.2 consistency model.
    public string PersistGraph(Graph graph, string? title)
    {
        var id = Guid.NewGuid().ToString("n");
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var g = conn.CreateCommand())
        {
            g.CommandText = "INSERT INTO graphs (id, title, created_at, policy_json) VALUES ($id, $title, $createdAt, $policy);";
            g.Parameters.AddWithValue("$id", id);
            g.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
            g.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("o"));
            g.Parameters.AddWithValue("$policy", PlexusJson.Serialize(RoutingPolicy.Manual("claude-opus-4-8")));
            g.ExecuteNonQuery();
        }

        foreach (var node in graph.Nodes)
        {
            using var n = conn.CreateCommand();
            n.CommandText = """
                INSERT INTO nodes (id, graph_id, parent_id, role, created_at, blocks_json, raw, meta_json, merge_parents_json, kind, reasoning_json)
                VALUES ($id, $gid, $parent, $role, $createdAt, $blocks, $raw, $meta, $merge, $kind, $reasoning);
                """;
            n.Parameters.AddWithValue("$id", node.Id);
            n.Parameters.AddWithValue("$gid", id);
            n.Parameters.AddWithValue("$parent", (object?)node.ParentId ?? DBNull.Value);
            n.Parameters.AddWithValue("$role", node.Role);
            n.Parameters.AddWithValue("$kind", (object?)node.Kind ?? DBNull.Value);
            n.Parameters.AddWithValue("$createdAt", node.CreatedAt);
            n.Parameters.AddWithValue("$blocks", PlexusJson.Serialize(node.Blocks));
            n.Parameters.AddWithValue("$raw", node.Raw);
            n.Parameters.AddWithValue("$meta", node.Meta is null ? DBNull.Value : PlexusJson.Serialize(node.Meta));
            n.Parameters.AddWithValue("$merge", node.MergeParents is null ? DBNull.Value : PlexusJson.Serialize(node.MergeParents));
            n.Parameters.AddWithValue("$reasoning", node.Reasoning is null ? DBNull.Value : PlexusJson.Serialize(node.Reasoning));
            n.ExecuteNonQuery();
        }

        foreach (var e in graph.Edges)
        {
            if (e.Kind is null || e.Kind == Contract.ReasoningEdges.Grounds)
                continue; // structural / grounds — derived on load, not stored
            using var ed = conn.CreateCommand();
            ed.CommandText = "INSERT INTO edges (graph_id, from_id, to_id, kind, weight) VALUES ($gid, $from, $to, $kind, $weight);";
            ed.Parameters.AddWithValue("$gid", id);
            ed.Parameters.AddWithValue("$from", e.From);
            ed.Parameters.AddWithValue("$to", e.To);
            ed.Parameters.AddWithValue("$kind", e.Kind);
            ed.Parameters.AddWithValue("$weight", (object?)e.Weight ?? DBNull.Value);
            ed.ExecuteNonQuery();
        }

        tx.Commit();
        return id;
    }

    public void SetGraphPolicy(string graphId, RoutingPolicy policy)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE graphs SET policy_json = $policy WHERE id = $id;";
        cmd.Parameters.AddWithValue("$policy", PlexusJson.Serialize(policy));
        cmd.Parameters.AddWithValue("$id", graphId);
        cmd.ExecuteNonQuery();
    }

    public Graph? LoadGraph(string graphId)
    {
        using var conn = Open();

        string? title;
        RoutingPolicy? policy = null;
        using (var gcmd = conn.CreateCommand())
        {
            gcmd.CommandText = "SELECT title, policy_json FROM graphs WHERE id = $id;";
            gcmd.Parameters.AddWithValue("$id", graphId);
            using var greader = gcmd.ExecuteReader();
            if (!greader.Read())
                return null;
            title = greader.IsDBNull(0) ? null : greader.GetString(0);
            if (!greader.IsDBNull(1))
                policy = PlexusJson.Deserialize<RoutingPolicy>(greader.GetString(1));
        }

        var graph = new Graph { Id = graphId, Title = title, DefaultPolicy = policy };

        using (var ncmd = conn.CreateCommand())
        {
            ncmd.CommandText = """
                SELECT id, parent_id, role, created_at, blocks_json, raw, meta_json, merge_parents_json, kind, reasoning_json
                FROM nodes WHERE graph_id = $gid ORDER BY created_at ASC;
                """;
            ncmd.Parameters.AddWithValue("$gid", graphId);
            using var reader = ncmd.ExecuteReader();
            while (reader.Read())
            {
                var node = new Node
                {
                    Id = reader.GetString(0),
                    ParentId = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Role = reader.GetString(2),
                    CreatedAt = reader.GetString(3),
                    // Upconvert any legacy block shapes (e.g. old charts) before
                    // deserializing, so persisted graphs load under the current contract.
                    Blocks = PlexusJson.Deserialize<List<Block>>(BlockCatalog.MigrateLegacyJson(reader.GetString(4))) ?? new(),
                    Raw = reader.GetString(5),
                    Meta = reader.IsDBNull(6) ? null : PlexusJson.Deserialize<NodeMeta>(reader.GetString(6)),
                    MergeParents = reader.IsDBNull(7) ? null : PlexusJson.Deserialize<List<string>>(reader.GetString(7)),
                    Kind = reader.IsDBNull(8) ? null : reader.GetString(8),
                    Reasoning = reader.IsDBNull(9) ? null : PlexusJson.Deserialize<ReasoningMeta>(reader.GetString(9)),
                };
                graph.Nodes.Add(node);
                if (node.ParentId is not null)
                    graph.Edges.Add(new Edge { From = node.ParentId, To = node.Id });
                if (node.MergeParents is not null)
                    foreach (var p in node.MergeParents)
                        graph.Edges.Add(new Edge { From = p, To = node.Id });
            }
        }

        // Semantic reasoning edges (ADR-0002 R2.1), loaded from storage and merged with
        // the derived structural edges. `grounds` is not here — it's derived from a
        // fact's source_ref at compose time.
        using (var ecmd = conn.CreateCommand())
        {
            ecmd.CommandText = "SELECT from_id, to_id, kind, weight FROM edges WHERE graph_id = $gid;";
            ecmd.Parameters.AddWithValue("$gid", graphId);
            using var reader = ecmd.ExecuteReader();
            while (reader.Read())
                graph.Edges.Add(new Edge
                {
                    From = reader.GetString(0),
                    To = reader.GetString(1),
                    Kind = reader.GetString(2),
                    Weight = reader.IsDBNull(3) ? null : reader.GetDouble(3),
                });
        }

        // Derive grounds edges (ADR-0002 R2.2): a fact grounds on the source node its
        // source_ref names. grounds is NOT stored (it's recoverable from source_ref), so
        // it's re-derived here — keeping the persisted set to the relational edges only.
        var nodeIds = graph.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var fact in graph.Nodes.Where(n => n.Reasoning?.Role == Contract.ReasoningRoles.Fact))
        {
            var sref = fact.Reasoning?.SourceRef;
            if (!string.IsNullOrEmpty(sref) && nodeIds.Contains(sref))
                graph.Edges.Add(new Edge { From = fact.Id, To = sref, Kind = Contract.ReasoningEdges.Grounds });
        }

        return graph;
    }

    // Persist a graph's SEMANTIC edges (those with a kind). Replaces the stored set for
    // the graph, so re-saving an edited reasoning graph is idempotent. Structural edges
    // (kind == null) are skipped — they are derived from parentId on load.
    public void SaveEdges(string graphId, IEnumerable<Edge> edges)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM edges WHERE graph_id = $gid;";
            del.Parameters.AddWithValue("$gid", graphId);
            del.ExecuteNonQuery();
        }

        foreach (var e in edges)
        {
            if (e.Kind is null || e.Kind == Contract.ReasoningEdges.Grounds)
                continue; // structural OR grounds — derived (from parentId / source_ref), not stored
            using var ins = conn.CreateCommand();
            ins.CommandText = "INSERT INTO edges (graph_id, from_id, to_id, kind, weight) VALUES ($gid, $from, $to, $kind, $weight);";
            ins.Parameters.AddWithValue("$gid", graphId);
            ins.Parameters.AddWithValue("$from", e.From);
            ins.Parameters.AddWithValue("$to", e.To);
            ins.Parameters.AddWithValue("$kind", e.Kind);
            ins.Parameters.AddWithValue("$weight", (object?)e.Weight ?? DBNull.Value);
            ins.ExecuteNonQuery();
        }

        tx.Commit();
    }

    // Record (or replace) the adjudication for a graph ATOMICALLY (ADR-0002 Rx.2.0). A
    // single-row upsert inside a transaction: a DB-rejected write (e.g. an invalid decision
    // tripping the CHECK) rolls the transaction back, so a failed adjudicate leaves the
    // prior state intact — never a partial one. The timestamp is stamped here (server-side),
    // not trusted from the client. Returns the stored adjudication.
    public Adjudication SaveAdjudication(string graphId, string decision, string? note, string reviewer)
    {
        var adj = new Adjudication
        {
            Decision = decision,
            Note = note,
            Reviewer = reviewer,
            Timestamp = DateTimeOffset.UtcNow.ToString("o"),
        };
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO adjudications (graph_id, decision, note, reviewer, timestamp)
                VALUES ($gid, $decision, $note, $reviewer, $ts)
                ON CONFLICT(graph_id) DO UPDATE SET
                    decision = excluded.decision, note = excluded.note,
                    reviewer = excluded.reviewer, timestamp = excluded.timestamp;
                """;
            cmd.Parameters.AddWithValue("$gid", graphId);
            cmd.Parameters.AddWithValue("$decision", decision);
            cmd.Parameters.AddWithValue("$note", (object?)note ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$reviewer", reviewer);
            cmd.Parameters.AddWithValue("$ts", adj.Timestamp);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return adj;
    }

    public Adjudication? LoadAdjudication(string graphId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT decision, note, reviewer, timestamp FROM adjudications WHERE graph_id = $gid;";
        cmd.Parameters.AddWithValue("$gid", graphId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;
        return new Adjudication
        {
            Decision = reader.GetString(0),
            Note = reader.IsDBNull(1) ? null : reader.GetString(1),
            Reviewer = reader.GetString(2),
            Timestamp = reader.GetString(3),
        };
    }

    public void AddNode(string graphId, Node node)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO nodes (id, graph_id, parent_id, role, created_at, blocks_json, raw, meta_json, merge_parents_json, kind, reasoning_json)
            VALUES ($id, $gid, $parent, $role, $createdAt, $blocks, $raw, $meta, $merge, $kind, $reasoning);
            """;
        cmd.Parameters.AddWithValue("$id", node.Id);
        cmd.Parameters.AddWithValue("$gid", graphId);
        cmd.Parameters.AddWithValue("$parent", (object?)node.ParentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$role", node.Role);
        cmd.Parameters.AddWithValue("$kind", (object?)node.Kind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$createdAt", node.CreatedAt);
        cmd.Parameters.AddWithValue("$blocks", PlexusJson.Serialize(node.Blocks));
        cmd.Parameters.AddWithValue("$raw", node.Raw);
        cmd.Parameters.AddWithValue("$meta", node.Meta is null ? DBNull.Value : PlexusJson.Serialize(node.Meta));
        cmd.Parameters.AddWithValue("$merge", node.MergeParents is null ? DBNull.Value : PlexusJson.Serialize(node.MergeParents));
        cmd.Parameters.AddWithValue("$reasoning", node.Reasoning is null ? DBNull.Value : PlexusJson.Serialize(node.Reasoning));
        cmd.ExecuteNonQuery();
    }
}
