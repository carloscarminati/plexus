using System.Text.Json;
using Plexus.Sidecar.Services;

namespace Plexus.Sidecar.Routing;

// The provider/model registry. Pricing/capabilities are sourced from models.dev
// (never hand-maintained) and cached locally; a background service refreshes on
// a schedule. Provider configs live in ~/.plexus/providers.json (default: just
// Anthropic, enabled).
public sealed class ModelRegistry
{
    private const string ApiUrl = "https://models.dev/api.json";
    private static readonly TimeSpan MaxCacheAge = TimeSpan.FromHours(24);

    private readonly HttpClient _http;
    private readonly ILogger<ModelRegistry> _log;
    private readonly string _cachePath;
    private readonly string _providersPath;
    private readonly object _gate = new();

    private Dictionary<string, ModelMetadata> _models = new(StringComparer.OrdinalIgnoreCase);
    private List<ProviderConfig> _providers = new();

    public ModelRegistry(HttpClient http, ILogger<ModelRegistry> log)
    {
        _http = http;
        _log = log;
        var dir = Path.GetDirectoryName(GraphDir())!;
        Directory.CreateDirectory(dir);
        _cachePath = Path.Combine(dir, "models.json");
        _providersPath = Path.Combine(dir, "providers.json");
        _providers = LoadProviders();
    }

    private static string GraphDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".plexus", "_");

    public string DefaultProviderId =>
        _providers.FirstOrDefault(p => p.Enabled)?.Id ?? "anthropic";

    public IReadOnlyList<ProviderConfig> Providers => _providers;

    // Ensure metadata is available: load a fresh cache, else fetch. Best-effort —
    // on failure we still serve fallback pricing for known models.
    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_models.Count > 0)
            return;

        if (TryLoadCache(out var json, fresh: true) && Parse(json))
        {
            _log.LogInformation("Model registry loaded from cache ({Count} models).", _models.Count);
            return;
        }

        await RefreshAsync(ct);
    }

    // Fetch the latest metadata from models.dev and rewrite the cache.
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await _http.GetStringAsync(ApiUrl, ct);
            await File.WriteAllTextAsync(_cachePath, json, ct);
            if (Parse(json))
                _log.LogInformation("Model registry refreshed from models.dev ({Count} models).", _models.Count);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "models.dev refresh failed; falling back to cache/built-ins.");
            if (TryLoadCache(out var cached, fresh: false))
                Parse(cached);
        }
    }

    // Provider-scoped lookup — the correct one for curated candidates, whose
    // provider is known. Avoids matching a reseller that re-lists a model id
    // (e.g. "302ai/claude-sonnet-4-6-...") under the wrong provider/pricing.
    public ModelMetadata? GetMetadata(string providerId, string modelId)
    {
        lock (_gate)
        {
            if (_models.TryGetValue($"{providerId}/{modelId}", out var exact))
                return exact;
            // models.dev keys are often date-suffixed (claude-haiku-4-5-20251001);
            // match our alias by prefix, but only within the candidate's provider.
            var match = _models.Values.FirstOrDefault(m =>
                m.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase) &&
                m.Id.StartsWith(modelId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }
        return FallbackPricing.TryGetValue(modelId, out var fb) ? fb : null;
    }

    // Provider-agnostic lookup (best-effort) — kept for callers that only have a
    // model id. Prefers an exact id, then the curated-provider fallback table.
    public ModelMetadata? GetMetadata(string modelId)
    {
        lock (_gate)
        {
            if (_models.TryGetValue(modelId, out var exact))
                return exact;
        }
        return FallbackPricing.TryGetValue(modelId, out var fb) ? fb : null;
    }

    public double? EstimateCostUsd(string providerId, string modelId, int? tokensIn, int? tokensOut)
    {
        var meta = GetMetadata(providerId, modelId);
        if (meta is null)
            return null;
        var inCost = (tokensIn ?? 0) / 1_000_000.0 * meta.CostInPerMTok;
        var outCost = (tokensOut ?? 0) / 1_000_000.0 * meta.CostOutPerMTok;
        return Math.Round(inCost + outCost, 6);
    }

    private bool TryLoadCache(out string json, bool fresh)
    {
        json = "";
        if (!File.Exists(_cachePath))
            return false;
        if (fresh && DateTime.UtcNow - File.GetLastWriteTimeUtc(_cachePath) > MaxCacheAge)
            return false;
        try
        {
            json = File.ReadAllText(_cachePath);
            return json.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    // Parse models.dev api.json: { providerId: { id, models: { modelId: {...} } } }.
    private bool Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var map = new Dictionary<string, ModelMetadata>(StringComparer.OrdinalIgnoreCase);

            foreach (var providerProp in doc.RootElement.EnumerateObject())
            {
                var provider = providerProp.Value;
                var providerId = GetString(provider, "id") ?? providerProp.Name;
                if (!provider.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var modelProp in models.EnumerateObject())
                {
                    var m = modelProp.Value;
                    var id = GetString(m, "id") ?? modelProp.Name;

                    double costIn = 0, costOut = 0;
                    if (m.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Object)
                    {
                        costIn = GetDouble(cost, "input");
                        costOut = GetDouble(cost, "output");
                    }

                    int ctxWindow = 0, maxOut = 0;
                    if (m.TryGetProperty("limit", out var limit) && limit.ValueKind == JsonValueKind.Object)
                    {
                        ctxWindow = (int)GetDouble(limit, "context");
                        maxOut = (int)GetDouble(limit, "output");
                    }

                    var vision = false;
                    if (m.TryGetProperty("modalities", out var mod) &&
                        mod.TryGetProperty("input", out var inputs) && inputs.ValueKind == JsonValueKind.Array)
                    {
                        vision = inputs.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String &&
                            string.Equals(x.GetString(), "image", StringComparison.OrdinalIgnoreCase));
                    }

                    var toolCall = GetBool(m, "tool_call");
                    map[$"{providerId}/{id}"] = new ModelMetadata(
                        id, providerId, costIn, costOut, ctxWindow, maxOut,
                        ToolCall: toolCall,
                        // models.dev has no explicit structured-output flag; tool-calling
                        // models reliably support it, so use that as the proxy.
                        StructuredOutput: toolCall,
                        Reasoning: GetBool(m, "reasoning"),
                        Vision: vision);
                    // NOTE: intentionally NOT indexing by bare id — bare ids
                    // collide across providers (resellers re-list "claude-*"),
                    // which would mis-attribute a manual model to the wrong
                    // provider/pricing. Lookups are provider-scoped or fall back
                    // to the curated table.
                }
            }

            lock (_gate)
                _models = map;
            return map.Count > 0;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to parse models.dev payload.");
            return false;
        }
    }

    private List<ProviderConfig> LoadProviders()
    {
        try
        {
            if (File.Exists(_providersPath))
            {
                var list = JsonSerializer.Deserialize<List<ProviderConfig>>(
                    File.ReadAllText(_providersPath), Json.Options);
                if (list is { Count: > 0 })
                    return list;
            }
        }
        catch
        {
            // fall through to default
        }

        var defaults = new List<ProviderConfig> { new() { Id = "anthropic", Enabled = true } };
        try
        {
            File.WriteAllText(_providersPath, JsonSerializer.Serialize(defaults, Json.Options));
        }
        catch
        {
            // best-effort
        }
        return defaults;
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double GetDouble(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static bool GetBool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) && v.GetBoolean();

    // Pricing for current Anthropic models that may not yet be listed on
    // models.dev (post-cutoff releases). Primary source remains models.dev; this
    // is a thin stopgap so cost is visible. $/1M tokens.
    private static readonly Dictionary<string, ModelMetadata> FallbackPricing = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-opus-4-8"] = new("claude-opus-4-8", "anthropic", 5, 25, 1_000_000, 128_000, true, true, true, true),
        ["claude-opus-4-7"] = new("claude-opus-4-7", "anthropic", 5, 25, 1_000_000, 128_000, true, true, true, true),
        ["claude-opus-4-6"] = new("claude-opus-4-6", "anthropic", 5, 25, 1_000_000, 128_000, true, true, true, true),
        ["claude-sonnet-4-6"] = new("claude-sonnet-4-6", "anthropic", 3, 15, 1_000_000, 64_000, true, true, true, true),
        ["claude-haiku-4-5"] = new("claude-haiku-4-5", "anthropic", 1, 5, 200_000, 64_000, true, true, true, true),
    };
}
