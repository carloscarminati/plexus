namespace Plexus.Sidecar.Routing;

// Mirror of docs/spec-model-routing.md §1. The registry = user-configured
// providers × metadata pulled from models.dev.

public sealed class ProviderConfig
{
    public string Id { get; set; } = "";            // "anthropic", "openai", "ollama", ...
    public string Type { get; set; } = "anthropic"; // "anthropic" | "openai-compatible"
    public string? Label { get; set; }              // display name; falls back to Id
    public string? BaseUrl { get; set; }            // openai-compatible: gateway/self-hosted/Ollama URL
    public string? ModelId { get; set; }            // default model for this provider's picker
    public bool Enabled { get; set; } = true;
    // API key is NOT here — it lives in the OS keychain, referenced by provider id.
}

// The fields we consume from models.dev (api.json). Pricing is per 1M tokens.
public sealed record ModelMetadata(
    string Id,
    string ProviderId,
    double CostInPerMTok,
    double CostOutPerMTok,
    int ContextWindow,
    int MaxOutput,
    bool ToolCall,
    bool StructuredOutput,
    bool Reasoning,
    bool Vision);
