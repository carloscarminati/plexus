using Plexus.Sidecar.Routing;

namespace Plexus.Sidecar.Tests;

// The negative routing test the M0 work introduced the seam for (HeuristicRouter
// .SelectFrom) but never actually wrote: a turn that needs MCP tools must never be
// routed to a model that lacks tool calling — the capability filter (step 1) drops
// it, even if it is the cheapest candidate.
public class HeuristicRouterTests
{
    // A cheap model that CANNOT call tools (but can do everything else). Under
    // auto:cost it would win on price — unless the tool requirement filters it out.
    private static CandidateSet.Candidate CheapNoTools() => new(
        "cheap-no-tools", "test", CandidateSet.Tier.Small,
        new ModelMetadata("cheap-no-tools", "test",
            CostInPerMTok: 0.1, CostOutPerMTok: 0.1, ContextWindow: 200_000, MaxOutput: 8000,
            ToolCall: false, StructuredOutput: true, Reasoning: false, Vision: false));

    // A pricey model that CAN call tools.
    private static CandidateSet.Candidate PriceyCapable() => new(
        "pricey-capable", "test", CandidateSet.Tier.Large,
        new ModelMetadata("pricey-capable", "test",
            CostInPerMTok: 15.0, CostOutPerMTok: 75.0, ContextWindow: 200_000, MaxOutput: 8000,
            ToolCall: true, StructuredOutput: true, Reasoning: true, Vision: true));

    private static RoutingContext Ctx(bool needsTools) => new(
        Messages: new List<(string, string)> { ("user", "do something") },
        Requires: new RequestRequirements(ToolCall: needsTools, StructuredOutput: true, MinContext: 100),
        Policy: RoutingPolicy.Auto("cost"));

    [Fact]
    public void ToolTurn_NeverRoutesToAModelLackingToolCall()
    {
        var candidates = new List<CandidateSet.Candidate> { CheapNoTools(), PriceyCapable() };

        var choice = HeuristicRouter.SelectFrom(candidates, Ctx(needsTools: true));

        // The cheaper candidate cannot call tools, so even under auto:cost the router
        // must pick the capable (pricier) one — never the incapable one.
        Assert.Equal("pricey-capable", choice.ModelId);
        Assert.NotEqual("cheap-no-tools", choice.ModelId);
    }

    [Fact]
    public void NonToolTurn_PicksTheCheapest()
    {
        // Control: with no tool requirement the cheap model is fully capable, so
        // auto:cost picks it. This proves the tool requirement (not something else)
        // is what flips the choice in the negative test above.
        var candidates = new List<CandidateSet.Candidate> { CheapNoTools(), PriceyCapable() };

        var choice = HeuristicRouter.SelectFrom(candidates, Ctx(needsTools: false));

        Assert.Equal("cheap-no-tools", choice.ModelId);
    }
}
