using Microsoft.Extensions.AI;
using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Model;
using Plexus.Sidecar.Services;

namespace Plexus.Sidecar.Tests;

// Covers the two provider-generic seams introduced with multi-provider execution (#1):
// block emission (provider-agnostic parsing of a turn's text) and the IChatClient
// factory (provider → client selection). The tool-use loop itself is exercised by the
// live smoke; here we pin the pieces that are pure + deterministic.
public class ExecutionPipelineTests
{
    // A well-formed block envelope is parsed straight through the catalog.
    [Fact]
    public void ParseBlocks_ValidEnvelope_UsesCatalog()
    {
        var raw = """{ "blocks": [ { "type": "markdown", "text": "hello **world**" } ] }""";

        var blocks = BlockEmission.ParseBlocks(raw);

        var md = Assert.IsType<MarkdownBlock>(Assert.Single(blocks));
        Assert.Equal("hello **world**", md.Text);
    }

    // A ```json fence around the envelope is tolerated (the model sometimes wraps it).
    [Fact]
    public void ParseBlocks_FencedEnvelope_IsUnwrapped()
    {
        var raw = "Here you go:\n```json\n{ \"blocks\": [ { \"type\": \"markdown\", \"text\": \"x\" } ] }\n```";

        var blocks = BlockEmission.ParseBlocks(raw);

        var md = Assert.IsType<MarkdownBlock>(Assert.Single(blocks));
        Assert.Equal("x", md.Text);
    }

    // Non-JSON / malformed output never throws — it falls back to the heuristic parser
    // so a turn is always renderable (weaker providers that ignore the block prompt).
    [Theory]
    [InlineData("just some plain prose, no json at all")]
    [InlineData("{ not valid json")]
    [InlineData("{ \"blocks\": [ { \"type\": \"markdown\" } ] }")] // valid JSON, invalid block (missing text)
    public void ParseBlocks_Malformed_FallsBackWithoutThrowing(string raw)
    {
        var blocks = BlockEmission.ParseBlocks(raw);

        Assert.NotEmpty(blocks); // fallback always yields at least one renderable block
    }

    // The system prompt carries an ephemeral cache breakpoint under the exact key the
    // Anthropic adapter reads (GetCacheControl → "anthropic:cache_control"), so prompt
    // caching survives the move to the provider-generic loop. Regression guard: a
    // refactor that drops the key would silently disable caching (only a live smoke
    // would otherwise catch it).
    [Fact]
    public void SystemContent_CarriesEphemeralCacheBreakpoint()
    {
        var content = ChatTurnService.SystemContent();

        Assert.Equal(SystemPrompt.Text, content.Text);
        var props = Assert.IsAssignableFrom<IDictionary<string, object?>>(content.AdditionalProperties);
        Assert.True(props.TryGetValue("anthropic:cache_control", out var cc));
        Assert.IsType<Anthropic.Models.Messages.CacheControlEphemeral>(cc);
    }

    // Unknown providers fail loudly rather than silently routing to Anthropic.
    [Fact]
    public void Factory_UnknownProvider_Throws()
    {
        var factory = new ChatClientFactory(new KeychainService());

        Assert.Throws<InvalidOperationException>(() => factory.For("openai-compatible", "gpt-4o-mini"));
    }

    // The Anthropic provider (default + explicit id) builds a client when a key is present.
    [Theory]
    [InlineData(null)]
    [InlineData("anthropic")]
    public void Factory_Anthropic_WithKey_BuildsClient(string? providerId)
    {
        var prior = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-ant-test-key");
        try
        {
            var factory = new ChatClientFactory(new KeychainService());

            using var client = factory.For(providerId, "claude-haiku-4-5");

            Assert.NotNull(client);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", prior);
        }
    }

    // The OpenAI-compatible builder produces a client from explicit config (base URL +
    // key + model) — the path the provider CRUD will drive.
    [Fact]
    public void Factory_OpenAiCompatible_BuildsClient()
    {
        using var client = ChatClientFactory.OpenAiCompatible(
            "https://api.example.com/v1", "sk-test", "some-model");

        Assert.NotNull(client);
    }
}
