using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Plexus.Sidecar.Services;

namespace Plexus.Sidecar.Mcp;

// A discovered tool, tagged with its server + the server's policy.
public sealed class McpToolRef
{
    public required string ServerId { get; init; }
    public required string ServerName { get; init; }
    public required string? ServerPolicy { get; init; }
    public required McpClientTool Tool { get; init; }

    // Annotations are hints from an UNTRUSTED server — read-only means auto-run is
    // allowed; everything else (false / null / absent) is treated as gated.
    public bool ReadOnly => Tool.ProtocolTool.Annotations?.ReadOnlyHint == true;
    public bool Destructive => Tool.ProtocolTool.Annotations?.DestructiveHint == true;
}

// The MCP host: connects to user-configured servers (registry), discovers their
// tools, and executes tool calls. It NEVER connects to a URL that didn't come
// from the registry (no auto-discovery from tool results or model output).
public sealed class McpHost : IAsyncDisposable
{
    private readonly ILogger<McpHost> _log;
    private readonly KeychainService _keychain;
    private readonly string _registryPath;
    private readonly Dictionary<string, McpClient> _clients = new();
    private readonly List<McpToolRef> _tools = new();
    private readonly object _gate = new();

    public McpHost(ILogger<McpHost> log, KeychainService keychain)
    {
        _log = log;
        _keychain = keychain;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".plexus");
        Directory.CreateDirectory(dir);
        _registryPath = Path.Combine(dir, "mcp-servers.json");
    }

    public IReadOnlyList<McpToolRef> Tools
    {
        get { lock (_gate) return _tools.ToList(); }
    }

    public List<McpServerConfig> LoadRegistry()
    {
        try
        {
            if (File.Exists(_registryPath))
                return Json.Deserialize<List<McpServerConfig>>(File.ReadAllText(_registryPath)) ?? new();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to read MCP registry.");
        }
        return new();
    }

    // Connect every enabled server. A server that fails to connect is logged and
    // skipped — it must not break startup or a turn.
    public async Task ConnectAllAsync(CancellationToken ct = default)
    {
        foreach (var server in LoadRegistry().Where(s => s.Enabled))
        {
            try
            {
                var client = await ConnectAsync(server, ct);
                var tools = await client.ListToolsAsync(cancellationToken: ct);
                lock (_gate)
                {
                    _clients[server.Id] = client;
                    foreach (var t in tools)
                        _tools.Add(new McpToolRef { ServerId = server.Id, ServerName = server.Name, ServerPolicy = server.ToolPolicy, Tool = t });
                }
                _log.LogInformation("MCP server '{Server}' connected: {Count} tools.", server.Id, tools.Count);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "MCP server '{Server}' failed to connect; skipping.", server.Id);
            }
        }
    }

    private async Task<McpClient> ConnectAsync(McpServerConfig server, CancellationToken ct)
    {
        if (server.Transport.Kind == "http")
        {
            if (string.IsNullOrWhiteSpace(server.Transport.Url))
                throw new InvalidOperationException($"MCP server '{server.Id}' has no URL.");
            var headers = new Dictionary<string, string>();
            // Credential is read from the keychain by server id and sent as a bearer
            // token; never inline, never logged, never placed in the URL.
            var key = _keychain.GetKey($"mcp-{server.Id}");
            if (!string.IsNullOrWhiteSpace(key))
                headers["Authorization"] = $"Bearer {key}";
            var http = new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = server.Id,
                Endpoint = new Uri(server.Transport.Url),
                AdditionalHeaders = headers,
            });
            return await McpClient.CreateAsync(http, cancellationToken: ct);
        }

        // stdio: spawn the server as a child process.
        var stdio = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = server.Id,
            Command = server.Transport.Command ?? throw new InvalidOperationException($"MCP server '{server.Id}' has no command."),
            Arguments = server.Transport.Args ?? new(),
            EnvironmentVariables = server.Transport.Env,
        });
        return await McpClient.CreateAsync(stdio, cancellationToken: ct);
    }

    // Execute a tool. Gating happens BEFORE this is called (in ConversationService).
    // A disconnected/failing server returns an error string — it never throws into
    // the turn loop.
    public async Task<string> CallAsync(string serverId, string tool, IReadOnlyDictionary<string, JsonElement> args, CancellationToken ct = default)
    {
        McpClient? client;
        lock (_gate) _clients.TryGetValue(serverId, out client);
        if (client is null)
            return $"[error] MCP server '{serverId}' is not connected.";

        try
        {
            var dict = args.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
            var result = await client.CallToolAsync(tool, dict, cancellationToken: ct);
            var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
            if (string.IsNullOrEmpty(text))
                text = "(tool returned no text content)";
            return result.IsError == true ? $"[tool error] {text}" : text;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "MCP tool '{Server}/{Tool}' call failed.", serverId, tool);
            return $"[error] tool call failed: {ex.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<McpClient> clients;
        lock (_gate) clients = _clients.Values.ToList();
        foreach (var c in clients)
        {
            try { await c.DisposeAsync(); } catch { /* ignore */ }
        }
    }
}
