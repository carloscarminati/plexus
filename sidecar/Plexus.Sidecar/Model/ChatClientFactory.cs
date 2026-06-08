using System.ClientModel;
using Anthropic;
using Microsoft.Extensions.AI;
using OpenAI;
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

    public ChatClientFactory(KeychainService keychain) => _keychain = keychain;

    // Resolve a client for a routed (providerId, modelId). The Anthropic provider is
    // the migrated default; OpenAI-compatible providers (base URL + model + key) arrive
    // with the provider CRUD.
    public IChatClient For(string? providerId, string modelId)
    {
        if (string.IsNullOrEmpty(providerId) || string.Equals(providerId, "anthropic", StringComparison.OrdinalIgnoreCase))
        {
            var key = _keychain.GetAnthropicKey()
                ?? throw new InvalidOperationException("No Anthropic API key configured. Add it in Settings → Providers.");
            return new AnthropicClient { ApiKey = key }.AsIChatClient(modelId, MaxOutputTokens);
        }

        // OpenAI-compatible: read its base URL/model from the provider store + key from
        // the keychain by id. Wired when the provider CRUD lands (phase: provider config).
        throw new InvalidOperationException($"Provider '{providerId}' is not yet configured for execution.");
    }

    // OpenAI-compatible client from explicit config (base URL + key + model). The
    // OpenAI SDK's custom Endpoint is what makes any OpenAI-compatible server work.
    public static IChatClient OpenAiCompatible(string baseUrl, string apiKey, string modelId)
    {
        var client = new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = new Uri(baseUrl) });
        return client.GetChatClient(modelId).AsIChatClient();
    }
}
