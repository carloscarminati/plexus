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

// App settings (confirm timeout, default routing policy) — local config file.
builder.Services.AddSingleton<SettingsStore>();

// MCP host (M0): connects only to user-configured registry servers.
builder.Services.AddSingleton<KeychainService>();
builder.Services.AddSingleton<McpHost>();

// Conversation services are always registered: the API key is resolved lazily per
// turn (KeychainService), so a key set from Settings → Providers takes effect on
// the next turn without restarting. Without a key, turns surface a clear error.
builder.Services.AddSingleton<AnthropicTurnService>();
builder.Services.AddSingleton<ConversationService>();

var app = builder.Build();
app.UseWebSockets();

var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Plexus");
if (string.IsNullOrWhiteSpace(app.Services.GetRequiredService<KeychainService>().GetAnthropicKey()))
    logger.LogWarning("No Anthropic API key found (keychain or ANTHROPIC_API_KEY). Set one in Settings → Providers.");

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
        context.RequestServices.GetRequiredService<ConversationService>(),
        context.RequestServices.GetRequiredService<ModelRegistry>(),
        context.RequestServices.GetRequiredService<SettingsStore>(),
        context.RequestServices.GetRequiredService<KeychainService>(),
        context.RequestServices.GetRequiredService<McpHost>(),
        logger);
    await hub.RunAsync(context.RequestAborted);
});

// Connect configured MCP servers before serving (per-server failures are logged
// and skipped inside ConnectAllAsync — a bad server never blocks startup).
await app.Services.GetRequiredService<McpHost>().ConnectAllAsync();

logger.LogInformation("Plexus sidecar listening on ws://127.0.0.1:8765/ws");
app.Run();
