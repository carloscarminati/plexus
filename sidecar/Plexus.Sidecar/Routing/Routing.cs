namespace Plexus.Sidecar.Routing;

// The routing seam (SPEC-model-routing.md §2). Introduce the seam now, the
// intelligence later: ManualRouter today, HeuristicRouter/LearnedRouter behind
// the same interface in R1/R2.

public sealed record RoutingPolicy(string Kind, string? ModelId = null, string? Objective = null, double? BudgetPerTurn = null)
{
    public static RoutingPolicy Manual(string modelId) => new("manual", ModelId: modelId);
    public static RoutingPolicy Auto(string objective, double? budgetPerTurn = null) =>
        new("auto", Objective: objective, BudgetPerTurn: budgetPerTurn);
}

// Hard requirements derived from the request. We always need structured output
// for block-emission strategy (a).
public sealed record RequestRequirements(
    bool ToolCall = false,
    bool StructuredOutput = true,
    bool Vision = false,
    int MinContext = 0);

public sealed record RoutingContext(
    IReadOnlyList<(string Role, string Content)> Messages,
    RequestRequirements Requires,
    RoutingPolicy Policy);

public sealed record ModelChoice(string ModelId, string ProviderId, string Reason);

public interface IModelRouter
{
    Task<ModelChoice> SelectModelAsync(RoutingContext ctx, CancellationToken ct = default);
}

// R0: returns the manually chosen model, but still runs the capability filter as
// a guardrail — if the chosen model can't meet `requires`, the reason carries a
// warning (we don't override the user's explicit choice).
public sealed class ManualRouter : IModelRouter
{
    private readonly ModelRegistry _registry;

    public ManualRouter(ModelRegistry registry) => _registry = registry;

    public Task<ModelChoice> SelectModelAsync(RoutingContext ctx, CancellationToken ct = default)
    {
        var modelId = ctx.Policy.ModelId
            ?? throw new InvalidOperationException("ManualRouter requires policy.ModelId.");

        var meta = _registry.GetMetadata(modelId);
        var providerId = meta?.ProviderId ?? _registry.DefaultProviderId;

        var unmet = meta is null ? new List<string>() : Unmet(meta, ctx.Requires);
        var reason = unmet.Count == 0
            ? $"manual: {modelId}"
            : $"manual: {modelId} (warning: may not support {string.Join(", ", unmet)})";

        return Task.FromResult(new ModelChoice(modelId, providerId, reason));
    }

    // Step 1 of the two-step every router follows: hard capability filter.
    internal static List<string> Unmet(ModelMetadata m, RequestRequirements req)
    {
        var unmet = new List<string>();
        if (req.ToolCall && !m.ToolCall) unmet.Add("tool calling");
        if (req.StructuredOutput && !m.StructuredOutput) unmet.Add("structured output");
        if (req.Vision && !m.Vision) unmet.Add("vision");
        if (req.MinContext > 0 && m.ContextWindow > 0 && m.ContextWindow < req.MinContext)
            unmet.Add($"context ≥ {req.MinContext}");
        return unmet;
    }
}
