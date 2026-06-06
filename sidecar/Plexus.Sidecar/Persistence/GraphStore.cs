using Microsoft.Data.Sqlite;
using Plexus.Sidecar.Contract;
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
                created_at  TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS nodes (
                id          TEXT PRIMARY KEY,
                graph_id    TEXT NOT NULL REFERENCES graphs(id) ON DELETE CASCADE,
                parent_id   TEXT,
                role        TEXT NOT NULL,
                created_at  TEXT NOT NULL,
                blocks_json TEXT NOT NULL,
                raw         TEXT NOT NULL,
                meta_json   TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_nodes_graph ON nodes(graph_id);
            """;
        cmd.ExecuteNonQuery();
    }

    public List<GraphSummary> ListGraphs()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, title FROM graphs ORDER BY created_at DESC;";
        using var reader = cmd.ExecuteReader();
        var result = new List<GraphSummary>();
        while (reader.Read())
        {
            result.Add(new GraphSummary
            {
                Id = reader.GetString(0),
                Title = reader.IsDBNull(1) ? null : reader.GetString(1),
            });
        }
        return result;
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
        };
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO graphs (id, title, created_at) VALUES ($id, $title, $createdAt);";
        cmd.Parameters.AddWithValue("$id", graph.Id);
        cmd.Parameters.AddWithValue("$title", (object?)graph.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
        return graph;
    }

    public Graph? LoadGraph(string graphId)
    {
        using var conn = Open();

        string? title;
        using (var gcmd = conn.CreateCommand())
        {
            gcmd.CommandText = "SELECT title FROM graphs WHERE id = $id;";
            gcmd.Parameters.AddWithValue("$id", graphId);
            using var greader = gcmd.ExecuteReader();
            if (!greader.Read())
                return null;
            title = greader.IsDBNull(0) ? null : greader.GetString(0);
        }

        var graph = new Graph { Id = graphId, Title = title };

        using (var ncmd = conn.CreateCommand())
        {
            ncmd.CommandText = """
                SELECT id, parent_id, role, created_at, blocks_json, raw, meta_json
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
                    Blocks = Json.Deserialize<List<Block>>(reader.GetString(4)) ?? new(),
                    Raw = reader.GetString(5),
                    Meta = reader.IsDBNull(6) ? null : Json.Deserialize<NodeMeta>(reader.GetString(6)),
                };
                graph.Nodes.Add(node);
                if (node.ParentId is not null)
                    graph.Edges.Add(new Edge { From = node.ParentId, To = node.Id });
            }
        }

        return graph;
    }

    public void AddNode(string graphId, Node node)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO nodes (id, graph_id, parent_id, role, created_at, blocks_json, raw, meta_json)
            VALUES ($id, $gid, $parent, $role, $createdAt, $blocks, $raw, $meta);
            """;
        cmd.Parameters.AddWithValue("$id", node.Id);
        cmd.Parameters.AddWithValue("$gid", graphId);
        cmd.Parameters.AddWithValue("$parent", (object?)node.ParentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$role", node.Role);
        cmd.Parameters.AddWithValue("$createdAt", node.CreatedAt);
        cmd.Parameters.AddWithValue("$blocks", Json.Serialize(node.Blocks));
        cmd.Parameters.AddWithValue("$raw", node.Raw);
        cmd.Parameters.AddWithValue("$meta", node.Meta is null ? DBNull.Value : Json.Serialize(node.Meta));
        cmd.ExecuteNonQuery();
    }
}
