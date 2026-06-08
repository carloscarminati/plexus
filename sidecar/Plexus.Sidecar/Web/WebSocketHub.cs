using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Model;
using Plexus.Sidecar.Persistence;
using Plexus.Sidecar.Routing;
using Plexus.Sidecar.Services;

namespace Plexus.Sidecar.Web;

// One WebSocket connection. Owns the send lock (a socket can't be written
// concurrently) and dispatches ClientEvents to the right service, streaming
// ServerEvents back.
public sealed class WebSocketHub
{
    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly GraphStore _store;
    private readonly ConversationService _conversation;
    private readonly ModelRegistry _registry;
    private readonly SettingsStore _settings;
    private readonly KeychainService _keychain;
    private readonly Mcp.McpHost _mcp;
    private readonly ILogger _log;
    // Pending MCP tool confirmations, keyed by tool_use id (M0 §3.2). The turn
    // runs in the background and awaits these; the receive loop resolves them.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingConfirms = new();

    // If the user never answers a tool confirmation, the turn is cancelled rather
    // than hanging forever. Read live from settings (default 120s).
    private TimeSpan ConfirmTimeout => TimeSpan.FromSeconds(_settings.Current.ConfirmTimeoutSeconds);

    public WebSocketHub(
        WebSocket socket, GraphStore store, ConversationService conversation, ModelRegistry registry,
        SettingsStore settings, KeychainService keychain, Mcp.McpHost mcp, ILogger log)
    {
        _socket = socket;
        _store = store;
        _conversation = conversation;
        _registry = registry;
        _settings = settings;
        _keychain = keychain;
        _mcp = mcp;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (_socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var message = await ReceiveTextAsync(ct);
            if (message is null)
                break; // close frame or socket gone

            ClientEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize<ClientEvent>(message, PlexusJson.Options);
            }
            catch (JsonException ex)
            {
                await SendAsync(new ErrorServerEvent { Message = $"Bad event: {ex.Message}" });
                continue;
            }

            if (evt is null)
                continue;

            try
            {
                await DispatchAsync(evt, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error handling client event");
                await SendAsync(new ErrorServerEvent { Message = ex.Message });
            }
        }
    }

    private async Task DispatchAsync(ClientEvent evt, CancellationToken ct)
    {
        switch (evt)
        {
            case ListGraphsEvent:
                await SendAsync(new GraphsServerEvent { Graphs = _store.ListGraphs() });
                break;

            case NewGraphEvent ng:
                var created = _store.CreateGraph(ng.Title);
                // Apply the global default routing policy (Settings → Routing) to new graphs.
                var defaultPolicy = _settings.Current.DefaultPolicy;
                _store.SetGraphPolicy(created.Id, defaultPolicy);
                created.DefaultPolicy = defaultPolicy;
                await SendAsync(new GraphServerEvent { Graph = created }); // make it active
                await SendAsync(new GraphsServerEvent { Graphs = _store.ListGraphs() }); // refresh the list
                break;

            case LoadGraphEvent lg:
                var graph = _store.LoadGraph(lg.GraphId);
                if (graph is null)
                    await SendAsync(new ErrorServerEvent { Message = $"Graph '{lg.GraphId}' not found." });
                else
                    await SendAsync(new GraphServerEvent { Graph = graph });
                break;

            case SendMessageEvent sm:
                if (!HasAnthropicKey()) { await SendAsync(NoKeyError()); break; }
                // Run the turn in the background so the receive loop stays free to
                // process the user's tool-confirmation reply mid-turn.
                StartTurn(c => _conversation.RunTurnAsync(
                    sm.GraphId, sm.FromNodeId, sm.Text, SendAsync, sm.Policy, sm.FromNodeIds,
                    req => RequestConfirmAsync(req, c), c), ct);
                break;

            case IntentEvent intent:
                HandleIntent(intent, ct);
                break;

            case EscalateEvent esc:
                if (!HasAnthropicKey()) { await SendAsync(NoKeyError()); break; }
                // Re-run the input that produced `esc.NodeId` as a sibling branch
                // with a stronger model (R1 §4.2). Background, like send_message.
                StartTurn(c => _conversation.EscalateTurnAsync(
                    esc.GraphId, esc.NodeId, SendAsync, esc.Policy,
                    req => RequestConfirmAsync(req, c), c), ct);
                break;

            case SynthesizeEvent syn:
                if (!HasAnthropicKey()) { await SendAsync(NoKeyError()); break; }
                // X1: converge the selected branches into a deliverable node. Background.
                StartTurn(c => _conversation.SynthesizeAsync(
                    syn.GraphId, syn.FromNodeIds, SendAsync, syn.Policy,
                    req => RequestConfirmAsync(req, c), c), ct);
                break;

            case ToolConfirmationEvent tc:
                if (_pendingConfirms.TryRemove(tc.ToolUseId, out var tcs))
                    tcs.TrySetResult(tc.Approved);
                break;

            case SetSessionPolicyEvent sp:
                _store.SetGraphPolicy(sp.GraphId, sp.Policy);
                break;

            case SetGraphTitleEvent st:
                _store.SetGraphTitle(st.GraphId, string.IsNullOrWhiteSpace(st.Title) ? null : st.Title.Trim());
                await SendAsync(new GraphsServerEvent { Graphs = _store.ListGraphs() });
                break;

            case DeleteGraphEvent dg:
                _store.DeleteGraph(dg.GraphId);
                await SendAsync(new GraphsServerEvent { Graphs = _store.ListGraphs() });
                break;

            case ListModelsEvent:
                await SendAsync(new ModelsServerEvent { Models = CuratedModels() });
                break;

            case GetSettingsEvent:
                await SendAsync(BuildSettingsEvent());
                break;

            case SetGeneralSettingsEvent gs:
            {
                var s = _settings.Current;
                _settings.Save(new AppSettings
                {
                    ConfirmTimeoutSeconds = gs.ConfirmTimeoutSeconds > 0 ? gs.ConfirmTimeoutSeconds : s.ConfirmTimeoutSeconds,
                    DefaultPolicy = s.DefaultPolicy,
                });
                await SendAsync(BuildSettingsEvent());
                break;
            }

            case SetDefaultPolicyEvent dp:
            {
                var s = _settings.Current;
                _settings.Save(new AppSettings { ConfirmTimeoutSeconds = s.ConfirmTimeoutSeconds, DefaultPolicy = dp.Policy });
                await SendAsync(BuildSettingsEvent());
                break;
            }

            case SetAnthropicKeyEvent sk:
            {
                var ok = !string.IsNullOrWhiteSpace(sk.Key) && _keychain.SetKey("anthropic", sk.Key.Trim());
                if (!ok)
                    await SendAsync(new ErrorServerEvent { Message = "Could not store the key in the keychain (macOS only)." });
                await SendAsync(BuildSettingsEvent());
                break;
            }

            case DeleteAnthropicKeyEvent:
                _keychain.DeleteKey("anthropic");
                await SendAsync(BuildSettingsEvent());
                break;

            case SetMcpServerEvent ms:
                await HandleSetMcpServerAsync(ms);
                break;

            case DeleteMcpServerEvent dm:
                await HandleDeleteMcpServerAsync(dm.Id);
                break;
        }
    }

    private bool HasAnthropicKey() => _keychain.GetAnthropicKey() is not null;
    private static ErrorServerEvent NoKeyError() =>
        new() { Message = "No Anthropic API key configured. Add it in Settings → Providers." };

    // Consolidated, secret-free view of all config the Settings panel surfaces.
    private SettingsServerEvent BuildSettingsEvent()
    {
        var s = _settings.Current;
        var servers = _mcp.LoadRegistry().Select(cfg => new McpServerView
        {
            Id = cfg.Id,
            Name = cfg.Name,
            Enabled = cfg.Enabled,
            ToolPolicy = cfg.ToolPolicy,
            Transport = new McpTransportView
            {
                Kind = cfg.Transport.Kind,
                Command = cfg.Transport.Command,
                Args = cfg.Transport.Args,
                Env = cfg.Transport.Env,
                Url = cfg.Transport.Url,
            },
            HttpCredentialSet = cfg.Transport.Kind == "http" && _keychain.HasKey($"mcp-{cfg.Id}"),
        }).ToList();

        return new SettingsServerEvent
        {
            ConfirmTimeoutSeconds = s.ConfirmTimeoutSeconds,
            DefaultPolicy = s.DefaultPolicy,
            AnthropicKeyConfigured = _keychain.HasKey("anthropic"),
            McpServers = servers,
        };
    }

    private async Task HandleSetMcpServerAsync(SetMcpServerEvent ms)
    {
        var v = ms.Server;
        if (string.IsNullOrWhiteSpace(v.Id))
        {
            await SendAsync(new ErrorServerEvent { Message = "MCP server needs an id." });
            return;
        }

        var registry = _mcp.LoadRegistry();
        var cfg = registry.FirstOrDefault(x => x.Id == v.Id);
        if (cfg is null)
        {
            cfg = new Mcp.McpServerConfig { Id = v.Id };
            registry.Add(cfg);
        }
        cfg.Name = v.Name;
        cfg.Enabled = v.Enabled;
        cfg.ToolPolicy = string.IsNullOrWhiteSpace(v.ToolPolicy) ? null : v.ToolPolicy;
        cfg.Transport = new Mcp.McpTransport
        {
            Kind = v.Transport.Kind,
            Command = v.Transport.Command,
            Args = v.Transport.Args,
            Env = v.Transport.Env,
            Url = v.Transport.Url,
        };
        _mcp.SaveRegistry(registry); // secrets are NOT in here

        // HTTP credential (optional) goes to the keychain by server id, never the file.
        if (!string.IsNullOrWhiteSpace(ms.HttpCredential))
            _keychain.SetKey($"mcp-{v.Id}", ms.HttpCredential!.Trim());

        await _mcp.ReloadAsync(); // apply add/enable/disable live
        await SendAsync(BuildSettingsEvent());
    }

    private async Task HandleDeleteMcpServerAsync(string id)
    {
        var registry = _mcp.LoadRegistry();
        registry.RemoveAll(x => x.Id == id);
        _mcp.SaveRegistry(registry);
        _keychain.DeleteKey($"mcp-{id}"); // drop any stored HTTP credential
        await _mcp.ReloadAsync();
        await SendAsync(BuildSettingsEvent());
    }

    // An interactive block (currently `choices`) fired. The sidecar — not the
    // frontend — decides the next turn: we inject the chosen option as a new user
    // message and continue from the node that showed it (spec §4.4).
    private void HandleIntent(IntentEvent intent, CancellationToken ct)
    {
        if (!HasAnthropicKey())
        {
            _ = SendAsync(NoKeyError());
            return;
        }
        if (intent.Kind != "choice")
        {
            _ = SendAsync(new ErrorServerEvent { Message = $"Unknown intent kind: {intent.Kind}" });
            return;
        }
        var text = TryGetString(intent.Payload, "label") ?? TryGetString(intent.Payload, "id") ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            _ = SendAsync(new ErrorServerEvent { Message = "Choice intent missing a label." });
            return;
        }
        // Branch from the node that showed the choices (background, like send_message).
        StartTurn(c => _conversation.RunTurnAsync(
            intent.GraphId, intent.NodeId, text, SendAsync, intent.Policy, null,
            req => RequestConfirmAsync(req, c), c), ct);
    }

    // Run a turn off the receive loop so tool-confirmation replies can be processed
    // mid-turn. Errors are surfaced to the client.
    private void StartTurn(Func<CancellationToken, Task> run, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            try { await run(ct); }
            catch (OperationCanceledException)
            {
                // Confirmation timed out or the connection closed — end cleanly and
                // tell the client (best-effort; a no-op if the socket is already gone).
                try { await SendAsync(new ErrorServerEvent { Message = "Turn cancelled (tool confirmation not answered, or connection closed)." }); } catch { }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Turn failed");
                try { await SendAsync(new ErrorServerEvent { Message = ex.Message }); } catch { }
            }
        }, ct);
    }

    // Ask the user to approve a gated tool call, then wait for their reply (M0 §3.2).
    // A dropped connection resolves to "denied".
    private async Task<bool> RequestConfirmAsync(ToolConfirmRequest req, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingConfirms[req.ToolUseId] = tcs;
        try
        {
            await SendAsync(new ToolConfirmationRequestServerEvent
            {
                NodeId = req.NodeId,
                ToolUseId = req.ToolUseId,
                ServerId = req.ServerId,
                ServerName = req.ServerName,
                Tool = req.Tool,
                Args = req.Args,
                ReadOnly = req.ReadOnly,
            });
            // User reply → true/false. No reply within ConfirmTimeout, or the
            // connection closing (ct), → cancel the task so the turn unwinds cleanly.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(ConfirmTimeout);
            using (linked.Token.Register(() => tcs.TrySetCanceled(linked.Token)))
                return await tcs.Task;
        }
        finally
        {
            _pendingConfirms.TryRemove(req.ToolUseId, out _);
        }
    }

    // The curated candidate set (NOT the full models.dev catalog) for the
    // Manual model picker, with metadata for display.
    private List<ModelInfo> CuratedModels() =>
        CandidateSet.Build(_registry).Select(c => new ModelInfo
        {
            Id = c.ModelId,
            ProviderId = c.ProviderId,
            Tier = c.Tier.ToString().ToLowerInvariant(),
            CostInPerMTok = c.Meta?.CostInPerMTok ?? 0,
            CostOutPerMTok = c.Meta?.CostOutPerMTok ?? 0,
            ContextWindow = c.Meta?.ContextWindow ?? 0,
            ToolCall = c.Meta?.ToolCall ?? false,
            Vision = c.Meta?.Vision ?? false,
        }).ToList();

    private static string? TryGetString(System.Text.Json.JsonElement payload, string key)
    {
        if (payload.ValueKind == System.Text.Json.JsonValueKind.Object &&
            payload.TryGetProperty(key, out var prop) &&
            prop.ValueKind == System.Text.Json.JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    private async Task SendAsync(ServerEvent ev)
    {
        // Serialize against the base type so the polymorphic "type" discriminator is emitted.
        var json = JsonSerializer.Serialize(ev, typeof(ServerEvent), PlexusJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync();
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<string?> ReceiveTextAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            }
            catch (WebSocketException)
            {
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                return null;
            }

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                break;
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
