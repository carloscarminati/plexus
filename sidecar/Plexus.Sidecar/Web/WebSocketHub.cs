using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Model;
using Plexus.Sidecar.Persistence;
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
    private readonly ConversationService? _conversation;
    private readonly ILogger _log;

    public WebSocketHub(WebSocket socket, GraphStore store, ConversationService? conversation, ILogger log)
    {
        _socket = socket;
        _store = store;
        _conversation = conversation;
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
                evt = JsonSerializer.Deserialize<ClientEvent>(message, Json.Options);
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
                await SendAsync(new GraphServerEvent { Graph = created });
                break;

            case LoadGraphEvent lg:
                var graph = _store.LoadGraph(lg.GraphId);
                if (graph is null)
                    await SendAsync(new ErrorServerEvent { Message = $"Graph '{lg.GraphId}' not found." });
                else
                    await SendAsync(new GraphServerEvent { Graph = graph });
                break;

            case SendMessageEvent sm:
                if (_conversation is null)
                {
                    await SendAsync(new ErrorServerEvent
                    {
                        Message = "No Anthropic API key configured. Set ANTHROPIC_API_KEY or add it to the keychain.",
                    });
                    break;
                }
                await _conversation.RunTurnAsync(sm.GraphId, sm.FromNodeId, sm.Text, SendAsync, ct);
                break;

            case IntentEvent:
                await SendAsync(new ErrorServerEvent { Message = "Interactive intents are not implemented yet (P1)." });
                break;
        }
    }

    private async Task SendAsync(ServerEvent ev)
    {
        // Serialize against the base type so the polymorphic "type" discriminator is emitted.
        var json = JsonSerializer.Serialize(ev, typeof(ServerEvent), Json.Options);
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
