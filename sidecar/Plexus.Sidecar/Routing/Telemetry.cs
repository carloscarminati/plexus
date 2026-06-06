using Microsoft.Data.Sqlite;

namespace Plexus.Sidecar.Routing;

// Every model call is recorded (docs/spec-model-routing.md §3 R0). This is the data
// that must exist before R1 auto-routing can be validated — you can't tune
// routing without per-request cost.
public sealed record TelemetryRecord(
    string Timestamp,   // ISO-8601 UTC
    string GraphId,
    string NodeId,
    string ModelId,
    string ProviderId,
    int? TokensIn,
    int? TokensOut,
    double? CostUsd,
    long LatencyMs,
    string Policy,      // "manual" | "auto"
    string Reason);

public interface ITelemetrySink
{
    void Record(TelemetryRecord record);
}

// Logs each call and appends it to a `telemetry` table in the local DB so it's
// queryable for R1 analysis.
public sealed class SqliteTelemetrySink : ITelemetrySink
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteTelemetrySink> _log;

    public SqliteTelemetrySink(ILogger<SqliteTelemetrySink> log, string? dbPath = null)
    {
        _log = log;
        var path = dbPath ?? Persistence.GraphStore.DefaultDbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        Initialize();
    }

    private void Initialize()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS telemetry (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                ts          TEXT NOT NULL,
                graph_id    TEXT NOT NULL,
                node_id     TEXT NOT NULL,
                model_id    TEXT NOT NULL,
                provider_id TEXT NOT NULL,
                tokens_in   INTEGER,
                tokens_out  INTEGER,
                cost_usd    REAL,
                latency_ms  INTEGER NOT NULL,
                policy      TEXT NOT NULL,
                reason      TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void Record(TelemetryRecord r)
    {
        _log.LogInformation(
            "telemetry model={Model} provider={Provider} in={In} out={Out} cost=${Cost} latency={Latency}ms policy={Policy} reason=\"{Reason}\"",
            r.ModelId, r.ProviderId, r.TokensIn, r.TokensOut, r.CostUsd, r.LatencyMs, r.Policy, r.Reason);

        try
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO telemetry
                    (ts, graph_id, node_id, model_id, provider_id, tokens_in, tokens_out, cost_usd, latency_ms, policy, reason)
                VALUES ($ts, $g, $n, $m, $p, $ti, $to, $c, $l, $pol, $r);
                """;
            cmd.Parameters.AddWithValue("$ts", r.Timestamp);
            cmd.Parameters.AddWithValue("$g", r.GraphId);
            cmd.Parameters.AddWithValue("$n", r.NodeId);
            cmd.Parameters.AddWithValue("$m", r.ModelId);
            cmd.Parameters.AddWithValue("$p", r.ProviderId);
            cmd.Parameters.AddWithValue("$ti", (object?)r.TokensIn ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$to", (object?)r.TokensOut ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$c", (object?)r.CostUsd ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$l", r.LatencyMs);
            cmd.Parameters.AddWithValue("$pol", r.Policy);
            cmd.Parameters.AddWithValue("$r", r.Reason);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to persist telemetry record.");
        }
    }
}
