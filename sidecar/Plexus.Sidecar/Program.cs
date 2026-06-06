using Plexus.Sidecar.Model;
using Plexus.Sidecar.Persistence;
using Plexus.Sidecar.Services;
using Plexus.Sidecar.Web;

var builder = WebApplication.CreateBuilder(args);

// The sidecar is local-only: bind to loopback so nothing is exposed off-machine.
builder.WebHost.UseUrls("http://127.0.0.1:8765");

builder.Services.AddSingleton<GraphStore>(_ => new GraphStore());
builder.Services.AddHttpClient<LinkCardResolver>();

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
        logger);
    await hub.RunAsync(context.RequestAborted);
});

logger.LogInformation("Plexus sidecar listening on ws://127.0.0.1:8765/ws");
app.Run();
