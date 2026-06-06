namespace Plexus.Sidecar.Routing;

// The CURATED candidate set the auto-router chooses from. We do NOT route over
// the full models.dev catalog (7000+ models) — that's only metadata. Each
// enabled provider maps to a hand-picked small/mid/large trio; models.dev is
// used only to look up each one's cost/capabilities.
public static class CandidateSet
{
    public enum Tier { Small = 0, Mid = 1, Large = 2 }

    // provider id -> (small, mid, large) model ids. Extend per provider as they
    // are enabled. Only providers present here AND enabled in the registry
    // contribute candidates.
    private static readonly Dictionary<string, string[]> TierTable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["anthropic"] = ["claude-haiku-4-5", "claude-sonnet-4-6", "claude-opus-4-8"],
    };

    public sealed record Candidate(string ModelId, string ProviderId, Tier Tier, ModelMetadata? Meta);

    public static List<Candidate> Build(ModelRegistry registry)
    {
        var list = new List<Candidate>();
        foreach (var provider in registry.Providers.Where(p => p.Enabled))
        {
            if (!TierTable.TryGetValue(provider.Id, out var tiers))
                continue;
            for (var rank = 0; rank < tiers.Length; rank++)
            {
                var modelId = tiers[rank];
                list.Add(new Candidate(modelId, provider.Id, (Tier)rank, registry.GetMetadata(provider.Id, modelId)));
            }
        }
        return list;
    }
}
