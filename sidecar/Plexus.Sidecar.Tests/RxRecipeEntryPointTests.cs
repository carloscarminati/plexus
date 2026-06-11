using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Mcp;
using Plexus.Sidecar.Model;
using Plexus.Sidecar.Persistence;
using Plexus.Sidecar.Routing;
using Plexus.Sidecar.Services;
using Plexus.Sidecar.Web;

namespace Plexus.Sidecar.Tests;

// ADR-0002 Rx (walking skeleton) — the recipe engine is reachable from the REAL WS
// event+persistence surface, not just the smoke harness. Driven through the actual
// WebSocketHub dispatch (a fake socket feeds the dev trigger + captures replies), with
// a stub provider — so we prove the entry point is a new CALLER, not new behavior.
public class RxRecipeEntryPointTests
{
    private const string Model = "claude-haiku-4-5";
    private const string Case = "CASE: A grader engine was run over its rev limit and a bearing spun.";

    // A valid grounded investigator run; facts cite ids from CuratedFactSource's default
    // corpus (the source RecipeRunner uses).
    private static string[] Script() => new[]
    {
        """{"question":"Why did the engine fail?","scope":"Q1"}""",
        """{"facts":[{"claim":"The engine ran over its rev limit.","sourceKind":"doc","sourceRef":"ctrl-overspeed-04"},{"claim":"Over-revving cut lubrication and spun a bearing.","sourceKind":"doc","sourceRef":"ctrl-lube-01"}]}""",
        """{"uncertainties":[{"question":"Was the rev limit alarmed?"}]}""",
        """{"hypotheses":[{"statement":"Operational over-demand","addresses":["u0"]},{"statement":"Lubrication system fault","addresses":["u0"]}]}""",
        """{"weighings":[{"fact":"f0","hypothesis":"h0","stance":"supports","weight":0.8},{"fact":"f1","hypothesis":"h1","stance":"refutes","weight":0.3}]}""",
        """{"selects":"h0","cites":["f0"]}""",
    };

    // ── parity (the gate): the WS-path graph == the harness graph for the same input ──
    [Fact]
    public async Task WsPathGraph_MatchesHarnessGraph_ForSameInput()
    {
        var harness = (await RecipeExecutor.RunAsync(
            new ScriptedChatClient(Script()), Recipes.Investigator, Model, context: Case, factSource: new CuratedFactSource())).Graph;

        using var fx = new TempStore();
        var graphId = await DriveAsync(fx.Store, Trigger(Case));
        var ws = fx.Store.LoadGraph(graphId!)!;

        Assert.Equal(NodeKeys(harness), NodeKeys(ws)); // same nodes (id / reasoning role / source_ref)
        Assert.Equal(EdgeKeys(harness), EdgeKeys(ws)); // same edges (from / to / kind / weight)
    }

    // ── round-trip persistence: R1 sees the same thing after the real persist path ──
    [Fact]
    public async Task PersistedGraph_RoundTrips_WithIdenticalR1Diagnostics()
    {
        var harness = (await RecipeExecutor.RunAsync(
            new ScriptedChatClient(Script()), Recipes.Investigator, Model, context: Case, factSource: new CuratedFactSource())).Graph;
        var before = ReasoningGraphValidator.Validate(harness);

        using var fx = new TempStore();
        var ws = fx.Store.LoadGraph((await DriveAsync(fx.Store, Trigger(Case)))!)!;
        var after = ReasoningGraphValidator.Validate(ws);

        Assert.Equal(before.HasErrors, after.HasErrors);
        Assert.Equal(before.HasFlags, after.HasFlags);
        Assert.Equal(before.Diagnostics.Count, after.Diagnostics.Count);
        Assert.Equal(before.OpenUncertainties.OrderBy(x => x), after.OpenUncertainties.OrderBy(x => x));
    }

    // ── R1 runs on the graph that came out of the real path ──
    [Fact]
    public async Task R1_RunsOnTheRealPathGraph_AndItIsSound()
    {
        using var fx = new TempStore();
        var ws = fx.Store.LoadGraph((await DriveAsync(fx.Store, Trigger(Case)))!)!;

        var v = ReasoningGraphValidator.Validate(ws);
        Assert.False(v.HasErrors);
        Assert.False(v.HasFlags);
        Assert.Contains(ws.Nodes, n => n.Reasoning?.Role == ReasoningRoles.Conclusion);
        Assert.Contains(ws.Edges, e => e.Kind == ReasoningEdges.Grounds); // grounded via the real path
    }

    // ── negative control: a malformed trigger → clean error event, no partial graph ──
    [Theory]
    [InlineData("")]                 // missing caseText
    [InlineData("__nonsense__")]     // unknown recipeId (with caseText present)
    public async Task MalformedTrigger_EmitsError_AndPersistsNoGraph(string mode)
    {
        using var fx = new TempStore();
        var trigger = mode == "" ? Trigger("") : Trigger(Case, recipeId: "__nonsense__");

        var (sent, done) = await DriveCapturingAsync(fx.Store, trigger);

        Assert.Null(done);                                                  // no done event
        Assert.Contains(sent, e => e is ErrorServerEvent);                  // a clean error event
        Assert.Empty(fx.Store.ListGraphs());                               // and NO partial graph persisted
    }

    // ── the gap this fix closes: a run that fails MID-WAY (not just bad input) ────────
    // A late step that fails (here: the conclusion never emits valid output → explicit
    // exhaustion) → the run returns before persistence, so nothing is written. Proves
    // run-to-completion-then-atomic-persist: no orphaned partial graph after a run failure.
    [Fact]
    public async Task MidRunFailure_EmitsError_AndPersistsNoGraph()
    {
        using var fx = new TempStore();
        var truncated = Script().Take(5).ToArray(); // valid through evaluation; conclusion gets no valid reply

        var (sent, done) = await DriveCapturingAsync(fx.Store, Trigger(Case), () => new ScriptedChatClient(truncated));

        Assert.Null(done);
        Assert.Contains(sent, e => e is ErrorServerEvent);
        Assert.Empty(fx.Store.ListGraphs()); // ran 5 steps in memory, failed the 6th → zero persisted
    }

    // ── drive the REAL hub dispatch via a fake socket ────────────────────────────────
    private static async Task<string?> DriveAsync(GraphStore store, string trigger)
    {
        var (_, done) = await DriveCapturingAsync(store, trigger);
        return done?.GraphId;
    }

    private static async Task<(List<ServerEvent> Sent, RecipeRunDoneServerEvent? Done)> DriveCapturingAsync(
        GraphStore store, string trigger, Func<IChatClient>? makeClient = null)
    {
        var socket = new FakeWebSocket(trigger);
        var hub = BuildHub(socket, store, makeClient ?? (() => new ScriptedChatClient(Script())));
        await hub.RunAsync(CancellationToken.None);
        var sent = socket.Sent.Select(s => JsonSerializer.Deserialize<ServerEvent>(s, PlexusJson.Options)!).ToList();
        return (sent, sent.OfType<RecipeRunDoneServerEvent>().FirstOrDefault());
    }

    private static WebSocketHub BuildHub(FakeWebSocket socket, GraphStore store, Func<IChatClient> makeClient)
    {
        var keychain = new KeychainService();
        var registry = ProvidersTests.IsolatedRegistry();
        var mcp = new McpHost(NullLogger<McpHost>.Instance, keychain); // not connected
        var conversation = new ConversationService(
            store, new ChatTurnService(), new ChatClientFactory(keychain, registry),
            new LinkCardResolver(new HttpClient()),
            new CompositeRouter(new ManualRouter(registry), new HeuristicRouter(registry)),
            registry, new NoOpTelemetry(), mcp);
        var runner = new RecipeRunner(makeClient, store);
        return new WebSocketHub(socket, store, conversation, registry, new SettingsStore(), keychain, mcp, runner, NullLogger.Instance);
    }

    private static string Trigger(string caseText, string? recipeId = null) =>
        JsonSerializer.Serialize<ClientEvent>(new RunRecipeDevEvent { CaseText = caseText, RecipeId = recipeId }, PlexusJson.Options);

    private static List<string> NodeKeys(Graph g) =>
        g.Nodes.Select(n => $"{n.Id}|{n.Reasoning?.Role}|{n.Reasoning?.SourceRef}").OrderBy(x => x, StringComparer.Ordinal).ToList();

    private static List<string> EdgeKeys(Graph g) =>
        g.Edges.Select(e => $"{e.From}|{e.To}|{e.Kind}|{e.Weight}").OrderBy(x => x, StringComparer.Ordinal).ToList();

    private sealed class NoOpTelemetry : ITelemetrySink
    {
        public void Record(TelemetryRecord record) { }
    }

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

    // Minimal in-memory WebSocket: feeds canned text frames, then a close; captures sends.
    private sealed class FakeWebSocket : WebSocket
    {
        private readonly Queue<byte[]> _incoming;
        public List<string> Sent { get; } = new();
        private WebSocketState _state = WebSocketState.Open;

        public FakeWebSocket(params string[] incoming) =>
            _incoming = new Queue<byte[]>(incoming.Select(Encoding.UTF8.GetBytes));

        public override WebSocketState State => _state;

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            if (_incoming.Count == 0)
            {
                _state = WebSocketState.CloseReceived;
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            }
            var msg = _incoming.Dequeue();
            msg.CopyTo(buffer.Array!, buffer.Offset);
            return Task.FromResult(new WebSocketReceiveResult(msg.Length, WebSocketMessageType.Text, true));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            Sent.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
            return Task.CompletedTask;
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() { }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
    }
}
