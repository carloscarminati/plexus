using System.ClientModel;
using Anthropic;
using Microsoft.Extensions.AI;
using OpenAI;
using Plexus.Sidecar.Routing;
using Plexus.Sidecar.Services;

namespace Plexus.Sidecar.Model;

// Builds the right Microsoft.Extensions.AI IChatClient for the selected provider, so
// the provider-generic turn loop (ChatTurnService) is the same code on every backend
// (#1). Anthropic uses the SDK's own AsIChatClient adapter; OpenAI-compatible uses the
// OpenAI SDK with a custom Endpoint (which is what makes OpenAI + local/self-hosted
// servers work). API keys are resolved from the keychain by provider id.
public sealed class ChatClientFactory
{
    private const int MaxOutputTokens = 16000;
    private readonly KeychainService _keychain;
    private readonly ModelRegistry _registry;

    public ChatClientFactory(KeychainService keychain, ModelRegistry registry)
    {
        _keychain = keychain;
        _registry = registry;
    }

    // Resolve a client for a routed (providerId, modelId). Looks the provider up in
    // the registry: anthropic-type uses the SDK adapter (key from the keychain by id);
    // openai-compatible uses the OpenAI SDK with the provider's base URL.
    public IChatClient For(string? providerId, string modelId)
    {
        var provider = string.IsNullOrEmpty(providerId) ? null : _registry.GetProvider(providerId);

        // Default / anthropic: the migrated single-provider path. Treat an unknown
        // "anthropic" id (or no id at all) as the built-in Anthropic provider so an
        // existing setup with no providers.json keeps working.
        if (provider is null
            ? string.IsNullOrEmpty(providerId) || string.Equals(providerId, "anthropic", StringComparison.OrdinalIgnoreCase)
            : string.Equals(provider.Type, "anthropic", StringComparison.OrdinalIgnoreCase))
        {
            var keyId = provider?.Id ?? "anthropic";
            var key = _keychain.GetKey(keyId)
                ?? throw new InvalidOperationException("No Anthropic API key configured. Add it in Settings → Providers.");
            return new AnthropicClient { ApiKey = key }.AsIChatClient(modelId, MaxOutputTokens);
        }

        if (provider is null)
            throw new InvalidOperationException($"Provider '{providerId}' is not configured.");

        if (string.Equals(provider.Type, "openai-compatible", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(provider.BaseUrl))
                throw new InvalidOperationException($"Provider '{provider.Id}' has no base URL configured.");
            var key = _keychain.GetKey(provider.Id)
                ?? throw new InvalidOperationException($"No API key configured for provider '{provider.Id}'. Add it in Settings → Providers.");
            return OpenAiCompatible(provider.BaseUrl, key, modelId);
        }

        throw new InvalidOperationException($"Provider '{provider.Id}' has an unknown type '{provider.Type}'.");
    }

    // OpenAI-compatible client from explicit config (base URL + key + model). The
    // OpenAI SDK's custom Endpoint is what makes any OpenAI-compatible server work.
    public static IChatClient OpenAiCompatible(string baseUrl, string apiKey, string modelId)
    {
        var client = new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = new Uri(baseUrl) });
        return client.GetChatClient(modelId).AsIChatClient();
    }
}
