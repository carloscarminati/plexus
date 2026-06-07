using System.Text.RegularExpressions;

namespace Plexus.Sidecar.Routing;

// R1 auto-routing. Behind the unchanged IModelRouter. Two steps (§2):
//   1. capability filter — drop curated candidates that can't meet `requires`
//      (structured output is ALWAYS required for block emission; tool/vision/
//      context as detected). Reuses ManualRouter.Unmet.
//   2. optimize by policy — cost: cheapest capable; quality: highest tier;
//      balanced: the complexity-appropriate tier, capped by budgetPerTurn.
public sealed partial class HeuristicRouter : IModelRouter
{
    private const int NominalOutputTokens = 800; // for pre-call budget estimates
    private readonly ModelRegistry _registry;

    public HeuristicRouter(ModelRegistry registry) => _registry = registry;

    public Task<ModelChoice> SelectModelAsync(RoutingContext ctx, CancellationToken ct = default) =>
        Task.FromResult(SelectFrom(CandidateSet.Build(_registry), ctx));

    // Pure selection over a candidate list (also the unit-test seam for the M0
    // negative routing test). Step 1: hard capability filter. Step 2: optimize by
    // policy among survivors.
    public static ModelChoice SelectFrom(IReadOnlyList<CandidateSet.Candidate> candidates, RoutingContext ctx)
    {
        var objective = ctx.Policy.Objective ?? "balanced";

        // Unknown metadata is allowed through (flags we can't see we can't rule on).
        var survivors = candidates
            .Where(c => c.Meta is null || ManualRouter.Unmet(c.Meta, ctx.Requires).Count == 0)
            .ToList();

        if (survivors.Count == 0)
        {
            var fallback = candidates.OrderByDescending(c => c.Tier).FirstOrDefault()
                ?? throw new InvalidOperationException("No candidate models configured.");
            return new ModelChoice(fallback.ModelId, fallback.ProviderId, $"auto/{objective}: no capable model — using {fallback.ModelId} (warning)");
        }

        var (floor, signals) = ComplexityTier(ctx);
        var choice = objective switch
        {
            "cost" => PickCheapest(survivors, ctx),
            "quality" => PickHighest(survivors),
            _ => PickBalanced(survivors, floor, ctx),
        };
        var reason = objective switch
        {
            "cost" => $"auto/cost: cheapest capable ({choice.ModelId})",
            "quality" => $"auto/quality: top tier ({choice.ModelId})",
            _ => $"auto/balanced: tier={floor.ToString().ToLowerInvariant()} for {signals} ({choice.ModelId})",
        };
        return new ModelChoice(choice.ModelId, choice.ProviderId, reason);
    }

    private static CandidateSet.Candidate PickCheapest(List<CandidateSet.Candidate> s, RoutingContext ctx) =>
        s.OrderBy(c => EstTurnCost(c, ctx)).ThenBy(c => c.Tier).First();

    private static CandidateSet.Candidate PickHighest(List<CandidateSet.Candidate> s) =>
        s.OrderByDescending(c => c.Tier).First();

    // Right-size to the complexity floor; if a budget is set and the floor model
    // exceeds it, step down to the cheapest survivor within budget.
    private static CandidateSet.Candidate PickBalanced(List<CandidateSet.Candidate> s, CandidateSet.Tier floor, RoutingContext ctx)
    {
        var atFloor = s.Where(c => c.Tier >= floor).OrderBy(c => c.Tier).FirstOrDefault()
                      ?? s.OrderByDescending(c => c.Tier).First();

        var budget = ctx.Policy.BudgetPerTurn;
        if (budget is null || EstTurnCost(atFloor, ctx) <= budget)
            return atFloor;

        var withinBudget = s.Where(c => EstTurnCost(c, ctx) <= budget).OrderByDescending(c => c.Tier).ToList();
        return withinBudget.Count > 0 ? withinBudget.First() : PickCheapest(s, ctx);
    }

    private static double EstTurnCost(CandidateSet.Candidate c, RoutingContext ctx)
    {
        if (c.Meta is null)
            return double.MaxValue; // unknown cost — never "cheapest"
        var inTok = Math.Max(0, ctx.Requires.MinContext);
        return inTok / 1_000_000.0 * c.Meta.CostInPerMTok + NominalOutputTokens / 1_000_000.0 * c.Meta.CostOutPerMTok;
    }

    [GeneratedRegex(@"```|(\bdef\b|\bfunction\b|\bclass\b|\bimport\b|=>|\{\s*$)", RegexOptions.Multiline)]
    private static partial Regex CodeHint();

    // Complexity signals -> tier, in one readable place (R1 §3):
    //   - prompt + reconstructed-history length
    //   - presence of code / attachments
    //   - requires.toolCall / structuredOutput
    //   - conversation depth (ancestor count)
    internal static (CandidateSet.Tier Tier, string Signals) ComplexityTier(RoutingContext ctx)
    {
        var lastUser = ctx.Messages.LastOrDefault(m => m.Role == "user").Content ?? "";
        var historyTokens = ctx.Requires.MinContext; // ~tokens of reconstructed history
        var depth = ctx.Messages.Count;
        var hasCode = CodeHint().IsMatch(lastUser);

        var score = 0;
        if (historyTokens > 8000) score += 2;
        else if (historyTokens > 1500) score += 1;
        if (hasCode) score += 1;
        if (ctx.Requires.ToolCall) score += 1;
        if (ctx.Requires.Vision) score += 1;
        if (depth >= 8) score += 2;
        else if (depth >= 4) score += 1;

        var tier = score >= 3 ? CandidateSet.Tier.Large
                 : score >= 1 ? CandidateSet.Tier.Mid
                 : CandidateSet.Tier.Small;

        var signals = $"len~{historyTokens}tok depth={depth}{(hasCode ? " code" : "")}{(ctx.Requires.ToolCall ? " tools" : "")} score={score}";
        return (tier, signals);
    }
}
