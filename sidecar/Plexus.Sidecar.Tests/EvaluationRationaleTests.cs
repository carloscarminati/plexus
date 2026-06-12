using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Model;

namespace Plexus.Sidecar.Tests;

// ADR-0002 F2 — the evaluation node carries a rationale (the qualitative "why" behind the
// weighing), not just a bare "Evaluation" placeholder. The verdict still derives from the
// edge weights; the rationale is additive — R1 never reads node content, so diagnostics are
// identical with or without it. F4 canonicalizes the rationale's refs like any prose.
public class EvaluationRationaleTests
{
    // A net-negative run (selected hypothesis is net-refuted → R1 flags it), so the
    // R1-neutral gate has a real diagnostic to preserve. Two variants differ ONLY in the
    // evaluation step: one emits a rationale (with model refs h0/f0), one omits it.
    private static string[] Script(bool withRationale)
    {
        var evaluation = withRationale
            ? """{"weighings":[{"fact":"f0","hypothesis":"h0","stance":"refutes","weight":0.9},{"fact":"f1","hypothesis":"h0","stance":"supports","weight":0.2}],"rationale":"h0 is weak: f0 refutes it strongly and only f1 lends thin support."}"""
            : """{"weighings":[{"fact":"f0","hypothesis":"h0","stance":"refutes","weight":0.9},{"fact":"f1","hypothesis":"h0","stance":"supports","weight":0.2}]}""";
        return new[]
        {
            """{"question":"q"}""",
            """{"facts":[{"claim":"A","sourceKind":"doc","sourceRef":"r1"},{"claim":"B","sourceKind":"api","sourceRef":"r2"}]}""",
            """{"uncertainties":[{"question":"u?"}]}""",
            """{"hypotheses":[{"statement":"H-zero","addresses":["u0"]},{"statement":"H-one","addresses":["u0"]}]}""",
            evaluation,
            """{"selects":"h0","cites":["f0"]}""",
        };
    }

    private static Node EvalNode(Graph g) => g.Nodes.Single(n => n.Reasoning?.Role == ReasoningRoles.Evaluation);

    private static async Task<Graph> RunAsync(bool withRationale) =>
        (await RecipeExecutor.RunAsync(new ScriptedChatClient(Script(withRationale)), Recipes.Investigator, "test-model")).Graph;

    // ── emission: the rationale lands in the node; the weighed edges are unchanged ──
    [Fact]
    public async Task EvaluationStep_CapturesRationale_IntoTheNode_EdgesUnchanged()
    {
        var g = await RunAsync(withRationale: true);

        var eval = EvalNode(g);
        Assert.NotEqual("Evaluation", eval.Raw);       // no longer the placeholder
        Assert.Contains("is weak", eval.Raw);          // the rationale text is the node content

        // The weighing edges are exactly the two emitted (cardinality/bounds untouched).
        var weighed = g.Edges.Where(e => e.Kind == ReasoningEdges.Supports || e.Kind == ReasoningEdges.Refutes).ToList();
        Assert.Equal(2, weighed.Count);
        Assert.Single(weighed, e => e.Kind == ReasoningEdges.Refutes && e.Weight == 0.9);
        Assert.Single(weighed, e => e.Kind == ReasoningEdges.Supports && e.Weight == 0.2);
    }

    // ── R1-neutral (the gate): same graph with and without the rationale → same R1 ──
    [Fact]
    public async Task Rationale_IsR1Neutral_DiagnosticsIdenticalWithAndWithout()
    {
        var withR = await RunAsync(withRationale: true);
        var without = await RunAsync(withRationale: false);

        var d1 = ReasoningGraphValidator.Validate(withR);
        var d2 = ReasoningGraphValidator.Validate(without);

        Assert.True(d1.HasFlags); // the net-negative flag IS there to preserve
        Assert.Equal(d2.HasFlags, d1.HasFlags);
        Assert.Equal(Key(d2), Key(d1));
        Assert.Equal(d2.OpenUncertainties.OrderBy(x => x), d1.OpenUncertainties.OrderBy(x => x));

        // And the rationale is the only difference: one node has it, the other is the placeholder.
        Assert.NotEqual("Evaluation", EvalNode(withR).Raw);
        Assert.Equal("Evaluation", EvalNode(without).Raw);

        static IEnumerable<string> Key(ReasoningValidationResult r) =>
            r.Diagnostics.Select(d => $"{d.Severity}:{d.Code}:{d.NodeId}").OrderBy(x => x);
    }

    // ── F4 reaches the rationale: its model refs are canonicalized to persisted ids ──
    [Fact]
    public async Task Rationale_RefsAreCanonicalized_ByF4()
    {
        var g = await RunAsync(withRationale: true);
        var eval = EvalNode(g);

        var firstHyp = g.Nodes.First(n => n.Reasoning?.Role == ReasoningRoles.Hypothesis).Id; // h0
        var firstFact = g.Nodes.First(n => n.Reasoning?.Role == ReasoningRoles.Fact).Id;       // f0

        Assert.Contains(firstHyp, eval.Raw);  // "h0" → persisted id
        Assert.Contains(firstFact, eval.Raw); // "f0" → persisted id
        // the bare model refs are gone (whole-word)
        Assert.DoesNotContain("h0", eval.Raw.Split(' ', ':', '.', ',', ';'));
        Assert.DoesNotContain("f0", eval.Raw.Split(' ', ':', '.', ',', ';'));
    }
}
