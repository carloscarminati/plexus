using Microsoft.Extensions.Logging.Abstractions;
using Plexus.Sidecar.Model;
using Plexus.Sidecar.Routing;
using Plexus.Sidecar.Services;

namespace Plexus.Sidecar.Tests;

// Phase 3 of #1: provider CRUD + openai-compatible execution. Exercises the
// provider store, the migration default, routing of a manually-chosen provider,
// the no-fabricated-cost rule, and the ChatClientFactory provider selection — all
// against an isolated config dir so the user's ~/.plexus is never touched.
public class ProvidersTests
{
    // A registry rooted at a throwaway temp dir (its own providers.json).
    internal static ModelRegistry IsolatedRegistry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "plexus-test-" + Guid.NewGuid().ToString("N"));
        return new ModelRegistry(new HttpClient(), NullLogger<ModelRegistry>.Instance, dir);
    }

    // Migration (additive): a fresh config seeds a default Anthropic provider whose
    // id maps to the legacy keychain entry — existing keys keep working untouched.
    [Fact]
    public void Migration_SeedsDefaultAnthropicProvider()
    {
        var reg = IsolatedRegistry();

        var anthropic = Assert.Single(reg.Providers);
        Assert.Equal("anthropic", anthropic.Id);
        Assert.Equal("anthropic", anthropic.Type);
        Assert.True(anthropic.Enabled);
    }

    // CRUD round-trip: add an openai-compatible provider, read it back, delete it.
    [Fact]
    public void Crud_AddReadDelete_OpenAiCompatible()
    {
        var reg = IsolatedRegistry();

        reg.SetProvider(new ProviderConfig
        {
            Id = "local-llm",
            Type = "openai-compatible",
            Label = "Local LLM",
            BaseUrl = "http://localhost:11434/v1",
            ModelId = "llama-3.1-70b",
        });

        var got = reg.GetProvider("local-llm");
        Assert.NotNull(got);
        Assert.Equal("openai-compatible", got!.Type);
        Assert.Equal("http://localhost:11434/v1", got.BaseUrl);
        Assert.Equal("llama-3.1-70b", got.ModelId);

        reg.DeleteProvider("local-llm");
        Assert.Null(reg.GetProvider("local-llm"));
    }

    // Upsert by id: setting the same id twice updates in place, never duplicates.
    [Fact]
    public void SetProvider_UpsertsByIdInPlace()
    {
        var reg = IsolatedRegistry();
        reg.SetProvider(new ProviderConfig { Id = "p", Type = "openai-compatible", Label = "first", BaseUrl = "http://x/v1" });
        reg.SetProvider(new ProviderConfig { Id = "p", Type = "openai-compatible", Label = "second", BaseUrl = "http://x/v1" });

        Assert.Single(reg.Providers.Where(x => x.Id == "p"));
        Assert.Equal("second", reg.GetProvider("p")!.Label);
    }

    // A manually-chosen provider id wins in routing — that's how an openai-compatible
    // model (absent from models.dev) reaches its own client instead of the default.
    [Fact]
    public async Task ManualRouter_HonorsExplicitProviderId()
    {
        var reg = IsolatedRegistry();
        var router = new ManualRouter(reg);
        var ctx = new RoutingContext(
            Array.Empty<(string, string)>(),
            new RequestRequirements(),
            RoutingPolicy.Manual("llama-3.1-70b", providerId: "local-llm"));

        var choice = await router.SelectModelAsync(ctx);

        Assert.Equal("local-llm", choice.ProviderId);
        Assert.Equal("llama-3.1-70b", choice.ModelId);
    }

    // No fabricated cost: an unknown (provider, model) pair returns null, surfaced as
    // "—" in the UI — never a made-up number.
    [Fact]
    public void EstimateCost_UnknownModel_ReturnsNull()
    {
        var reg = IsolatedRegistry();

        var cost = reg.EstimateCostUsd("local-llm", "llama-3.1-70b", tokensIn: 1000, tokensOut: 500);

        Assert.Null(cost);
    }

    // The factory routes an openai-compatible provider to the OpenAI SDK client
    // (key resolved by provider id; here via the env fallback to avoid the keychain).
    [Fact]
    public void Factory_OpenAiCompatibleProvider_BuildsClient()
    {
        var reg = IsolatedRegistry();
        reg.SetProvider(new ProviderConfig
        {
            Id = "myprov",
            Type = "openai-compatible",
            BaseUrl = "https://api.example.com/v1",
            ModelId = "some-model",
        });

        var prior = Environment.GetEnvironmentVariable("MYPROV_API_KEY");
        Environment.SetEnvironmentVariable("MYPROV_API_KEY", "sk-test");
        try
        {
            var factory = new ChatClientFactory(new KeychainService(), reg);

            using var client = factory.For("myprov", "some-model");

            Assert.NotNull(client);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MYPROV_API_KEY", prior);
        }
    }

    // An openai-compatible provider with no key configured fails loudly (and reaches
    // the openai branch — not a silent fallback to Anthropic).
    [Fact]
    public void Factory_OpenAiCompatible_NoKey_Throws()
    {
        var reg = IsolatedRegistry();
        reg.SetProvider(new ProviderConfig { Id = "nokey", Type = "openai-compatible", BaseUrl = "https://x/v1" });
        var factory = new ChatClientFactory(new KeychainService(), reg);

        var ex = Assert.Throws<InvalidOperationException>(() => factory.For("nokey", "m"));
        Assert.Contains("nokey", ex.Message);
    }
}
