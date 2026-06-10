using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Model;

namespace Plexus.Sidecar.Tests;

// ADR-0002 R2.0a — the auto-fix emission machinery, in ISOLATION: no recipe engine,
// no real provider, no real case. A scripted IChatClient stands in for the model so
// we can force an invalid emission and assert the loop's behavior. This is the NEW
// feature surface — separate from the factoring-is-behavior-neutral assertion (which
// the existing render/catalog suite proves).
public class SchemaConstrainedEmitterTests
{
    private const string ValidFact = """{"claim":"X happened","sourceKind":"doc","sourceRef":"catalog://1"}""";
    private const string MissingSourceRef = """{"claim":"X happened","sourceKind":"doc"}"""; // structurally invalid

    // Auto-fix recovers: an invalid first emission is re-prompted with the errors and
    // the corrected second emission validates.
    [Fact]
    public async Task AutoFix_RecoversInvalidThenValid()
    {
        var client = new ScriptedChatClient(MissingSourceRef, ValidFact);

        var r = await SchemaConstrainedEmitter.EmitAsync(
            client, "test-model", "Extract one fact.", ReasoningSchemas.Fact, maxAttempts: 3);

        Assert.True(r.Ok);
        Assert.Equal(2, r.Attempts);     // recovered on the second try
        Assert.Equal(2, client.Calls);   // re-prompted exactly once
        Assert.Equal("catalog://1", (string?)r.Value!["sourceRef"]);
    }

    // Negative control: emission never validates → bounded attempts exhaust and the
    // emitter returns an EXPLICIT error, never a silent degraded fallback.
    [Fact]
    public async Task AutoFix_ExhaustsAttempts_ReturnsExplicitError()
    {
        var client = new ScriptedChatClient(MissingSourceRef, MissingSourceRef);

        var r = await SchemaConstrainedEmitter.EmitAsync(
            client, "test-model", "Extract one fact.", ReasoningSchemas.Fact, maxAttempts: 2);

        Assert.False(r.Ok);
        Assert.Null(r.Value);
        Assert.Equal(2, r.Attempts);
        Assert.Equal(2, client.Calls);
        Assert.NotNull(r.Error);                       // explicit failure, not markdown
        Assert.NotEmpty(r.Errors);                     // carries the last schema errors
    }

    // The helper retargets: the reasoning fact schema enforces sourceRef structurally
    // (the layer R2.0a owns). An empty/non-resolving sourceRef is structurally VALID —
    // that's R1's semantic concern, exercised in R2.0b.
    [Fact]
    public void FactSchema_RequiresSourceRef_Structurally()
    {
        Assert.False(JsonSchemaGen.Validate(ReasoningSchemas.Fact, JsonNode.Parse(MissingSourceRef), out var errs));
        Assert.Contains(errs, e => e.Contains("sourceRef", StringComparison.OrdinalIgnoreCase));

        Assert.True(JsonSchemaGen.Validate(ReasoningSchemas.Fact, JsonNode.Parse(ValidFact), out _));
    }

    // A scripted model: returns canned replies in order (then "{}" once exhausted),
    // counting calls so a test can assert how many times the loop re-prompted.
    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Queue<string> _replies;
        public int Calls { get; private set; }

        public ScriptedChatClient(params string[] replies) => _replies = new Queue<string>(replies);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            var text = _replies.Count > 0 ? _replies.Dequeue() : "{}";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
