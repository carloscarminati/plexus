using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Model;

namespace Plexus.Sidecar.Tests;

// ADR-0002 escalate wire — when R1 flags the small-model graph (a net-negative
// selection), the contested tail (evaluation → conclusion) is re-run with a stronger
// model, keeping the sound front (facts/hypotheses) intact. This closes the
// "flagged → escalate" loop the live smoke surfaced: a flag must trigger something.
public class RecipeEscalateTests
{
    // Small-model run: the conclusion selects a hypothesis the evidence net-refutes.
    private static readonly string[] NetNegativeRun =
    {
        """{"question":"q"}""",
        """{"facts":[{"claim":"A","sourceKind":"doc","sourceRef":"r1"},{"claim":"B","sourceKind":"api","sourceRef":"r2"}]}""",
        """{"uncertainties":[{"question":"u?"}]}""",
        """{"hypotheses":[{"statement":"H0","addresses":["u0"]},{"statement":"H1","addresses":["u0"]}]}""",
        """{"weighings":[{"fact":"f0","hypothesis":"h0","stance":"refutes","weight":0.9},{"fact":"f1","hypothesis":"h0","stance":"supports","weight":0.2}]}""",
        """{"selects":"h0","cites":["f0"]}""",
    };

    // The stronger model's re-run of the tail: re-weighs the evidence net-positive, then
    // re-concludes — h0 now stands.
    private static readonly string[] EscalatedTail =
    {
        """{"weighings":[{"fact":"f0","hypothesis":"h0","stance":"supports","weight":0.8},{"fact":"f1","hypothesis":"h0","stance":"refutes","weight":0.1}]}""",
        """{"selects":"h0","cites":["f0"]}""",
    };

    // Baseline: no escalate model → the flag stands, nothing is escalated (opt-in).
    [Fact]
    public async Task WithoutEscalateModel_FlagStands_NothingEscalated()
    {
        var run = await RecipeExecutor.RunAsync(new ScriptedChatClient(NetNegativeRun), Recipes.Investigator, "small");

        Assert.True(run.Ok);
        Assert.Empty(run.EscalatedSteps!);
        Assert.True(ReasoningGraphValidator.Validate(run.Graph).HasFlags); // the net-negative flag is still there
    }

    // With an escalate model, the flag triggers a per-node re-run of evaluation +
    // conclusion; the re-weighing clears the flag, and the sound front is preserved.
    [Fact]
    public async Task NetNegativeFlag_EscalatesContestedTail_AndClearsTheFlag()
    {
        var client = new ScriptedChatClient(NetNegativeRun.Concat(EscalatedTail).ToArray());

        var run = await RecipeExecutor.RunAsync(client, Recipes.Investigator, "small", escalateModelId: "strong");

        Assert.True(run.Ok);
        Assert.Equal(new[] { "evaluation", "conclusion" }, run.EscalatedSteps);

        var v = ReasoningGraphValidator.Validate(run.Graph);
        Assert.False(v.HasFlags); // the contested selection was re-weighed net-positive

        // The sound front survived: facts + hypotheses are still there, and the escalated
        // evidence (supports 0.8) replaced the old refutes — exactly one evaluation node.
        Assert.Equal(2, run.Graph.Nodes.Count(n => n.Reasoning?.Role == ReasoningRoles.Fact));
        Assert.Equal(2, run.Graph.Nodes.Count(n => n.Reasoning?.Role == ReasoningRoles.Hypothesis));
        Assert.Single(run.Graph.Nodes, n => n.Reasoning?.Role == ReasoningRoles.Evaluation);
        Assert.Single(run.Graph.Edges, e => e.Kind == ReasoningEdges.Supports && e.Weight == 0.8);
    }
}
