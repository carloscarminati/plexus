using Plexus.Sidecar.Mcp;
using Plexus.Sidecar.Model;
using Plexus.Sidecar.Persistence;
using Plexus.Sidecar.Routing;
using Plexus.Sidecar.Services;
using Plexus.Sidecar.Web;

var builder = WebApplication.CreateBuilder(args);

// The sidecar is local-only: bind to loopback so nothing is exposed off-machine.
builder.WebHost.UseUrls("http://127.0.0.1:8765");

builder.Services.AddSingleton<GraphStore>(_ => new GraphStore());
builder.Services.AddHttpClient<LinkCardResolver>();

// Model routing (R0): registry (models.dev), the routing seam, telemetry. These
// run regardless of whether a conversation key is present. The registry is a
// singleton so the scheduled refresh and the turn path share loaded metadata.
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ModelRegistry>(sp => new ModelRegistry(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
    sp.GetRequiredService<ILogger<ModelRegistry>>()));
// R1: manual + heuristic behind one IModelRouter (CompositeRouter dispatches by policy).
builder.Services.AddSingleton<ManualRouter>();
builder.Services.AddSingleton<HeuristicRouter>();
builder.Services.AddSingleton<IModelRouter, CompositeRouter>();
builder.Services.AddSingleton<ITelemetrySink>(sp =>
    new SqliteTelemetrySink(sp.GetRequiredService<ILogger<SqliteTelemetrySink>>()));
builder.Services.AddHostedService<RegistryRefreshService>();

// MCP host (M0): connects only to user-configured registry servers.
builder.Services.AddSingleton<KeychainService>();
builder.Services.AddSingleton<McpHost>();

// Resolve the API key once at startup; it never leaves the sidecar. Conversation
// services are only registered when a key is present — without one the app still
// serves graph load/list/create, it just can't run turns.
var apiKey = new KeychainService().GetAnthropicKey();
if (!string.IsNullOrWhiteSpace(apiKey))
{
    builder.Services.AddSingleton(new AnthropicTurnService(apiKey));
    builder.Services.AddSingleton<ConversationService>();
}

var app = builder.Build();
app.UseWebSockets();

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Plexus");
if (string.IsNullOrWhiteSpace(apiKey))
    logger.LogWarning("No Anthropic API key found (keychain or ANTHROPIC_API_KEY). Conversations are disabled until one is set.");

app.MapGet("/health", () => Results.Ok(new { status = "ok", schemaVersion = Plexus.Sidecar.Contract.BlockSchema.Version }));

app.Map("/ws", async (HttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var hub = new WebSocketHub(
        socket,
        context.RequestServices.GetRequiredService<GraphStore>(),
        context.RequestServices.GetService<ConversationService>(), // null when no API key
        context.RequestServices.GetRequiredService<ModelRegistry>(),
        logger);
    await hub.RunAsync(context.RequestAborted);
});

// Connect configured MCP servers before serving (per-server failures are logged
// and skipped inside ConnectAllAsync — a bad server never blocks startup).
await app.Services.GetRequiredService<McpHost>().ConnectAllAsync();

logger.LogInformation("Plexus sidecar listening on ws://127.0.0.1:8765/ws");
app.Run();
