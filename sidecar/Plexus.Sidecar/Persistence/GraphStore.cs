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
            """;
        cmd.ExecuteNonQuery();

        // Migrations for older DBs (ALTER throws if the column already exists).
        foreach (var (table, col) in new[] { ("graphs", "policy_json TEXT"), ("nodes", "merge_parents_json TEXT") })
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
            SELECT g.id, g.title, COALESCE(MAX(n.created_at), g.created_at) AS updated_at
            FROM graphs g LEFT JOIN nodes n ON n.graph_id = g.id
            GROUP BY g.id, g.title, g.created_at
            ORDER BY updated_at DESC;
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
                SELECT id, parent_id, role, created_at, blocks_json, raw, meta_json, merge_parents_json
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
                    Blocks = PlexusJson.Deserialize<List<Block>>(reader.GetString(4)) ?? new(),
                    Raw = reader.GetString(5),
                    Meta = reader.IsDBNull(6) ? null : PlexusJson.Deserialize<NodeMeta>(reader.GetString(6)),
                    MergeParents = reader.IsDBNull(7) ? null : PlexusJson.Deserialize<List<string>>(reader.GetString(7)),
                };
                graph.Nodes.Add(node);
                if (node.ParentId is not null)
                    graph.Edges.Add(new Edge { From = node.ParentId, To = node.Id });
                if (node.MergeParents is not null)
                    foreach (var p in node.MergeParents)
                        graph.Edges.Add(new Edge { From = p, To = node.Id });
            }
        }

        return graph;
    }

    public void AddNode(string graphId, Node node)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO nodes (id, graph_id, parent_id, role, created_at, blocks_json, raw, meta_json, merge_parents_json)
            VALUES ($id, $gid, $parent, $role, $createdAt, $blocks, $raw, $meta, $merge);
            """;
        cmd.Parameters.AddWithValue("$id", node.Id);
        cmd.Parameters.AddWithValue("$gid", graphId);
        cmd.Parameters.AddWithValue("$parent", (object?)node.ParentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$role", node.Role);
        cmd.Parameters.AddWithValue("$createdAt", node.CreatedAt);
        cmd.Parameters.AddWithValue("$blocks", PlexusJson.Serialize(node.Blocks));
        cmd.Parameters.AddWithValue("$raw", node.Raw);
        cmd.Parameters.AddWithValue("$meta", node.Meta is null ? DBNull.Value : PlexusJson.Serialize(node.Meta));
        cmd.Parameters.AddWithValue("$merge", node.MergeParents is null ? DBNull.Value : PlexusJson.Serialize(node.MergeParents));
        cmd.ExecuteNonQuery();
    }
}
