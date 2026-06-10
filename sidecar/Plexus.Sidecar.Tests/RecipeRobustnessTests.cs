using System.Text.Json.Nodes;
using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Model;

namespace Plexus.Sidecar.Tests;

// ADR-0002 R2.0b robustness guards — the two things a real model does that a scripted
// one doesn't, hardened before the live smoke: emitted refs that don't resolve, and
// out-of-range weights. Both are caught WITHOUT silently dropping data (fatal in an
// auditability tool): refs feed the auto-fix loop then fail explicitly; weights are a
// structural bound.
public class RecipeRobustnessTests
{
    private static Recipe FrameFactsHypothesesEval() => new()
    {
        Id = "t",
        Steps =
        {
            new() { Id = "frame", Role = ReasoningRoles.Frame, Prompt = "frame" },
            new() { Id = "facts", Role = ReasoningRoles.Fact, Array = true, MinItems = 1, Prompt = "facts" },
            new() { Id = "hypotheses", Role = ReasoningRoles.Hypothesis, Array = true, MinItems = 2, Prompt = "hyps" },
            new() { Id = "evaluation", Role = ReasoningRoles.Evaluation, Prompt = "eval" },
        },
    };

    private const string Frame = """{"question":"q"}""";
    private const string OneFact = """{"facts":[{"claim":"a","sourceKind":"doc","sourceRef":"x://1"}]}""";
    private const string TwoHyps = """{"hypotheses":[{"statement":"h0"},{"statement":"h1"}]}""";
    private const string EvalBadRef = """{"weighings":[{"fact":"f0","hypothesis":"h7","stance":"supports","weight":0.8}]}""";  // h7 doesn't exist
    private const string EvalGoodRef = """{"weighings":[{"fact":"f0","hypothesis":"h0","stance":"supports","weight":0.8}]}""";

    // Guard #1: a dangling ref is re-prompted (with the valid refs) and corrected — the
    // edge is built from the fixed ref, never silently dropped.
    [Fact]
    public async Task UnresolvedRef_AutoFixes_NotSilentlyDropped()
    {
        var client = new ScriptedChatClient(Frame, OneFact, TwoHyps, EvalBadRef, EvalGoodRef);

        var run = await RecipeExecutor.RunAsync(client, FrameFactsHypothesesEval(), "test-model");

        Assert.True(run.Ok);
        Assert.Equal(5, client.Calls); // evaluation step re-prompted once (4 steps + 1 retry)
        Assert.Single(run.Graph.Edges, e => e.Kind == ReasoningEdges.Supports); // the corrected edge exists
        // and no supports edge points at a node that doesn't exist
        var ids = run.Graph.Nodes.Select(n => n.Id).ToHashSet();
        Assert.All(run.Graph.Edges.Where(e => e.Kind == ReasoningEdges.Supports), e => Assert.Contains(e.To, ids));
    }

    // Guard #1: if the model never fixes the ref, the run fails EXPLICITLY at that step
    // — the bad emission is surfaced, not swallowed.
    [Fact]
    public async Task UnresolvedRef_Exhausts_FailsExplicitly()
    {
        var client = new ScriptedChatClient(Frame, OneFact, TwoHyps, EvalBadRef, EvalBadRef, EvalBadRef);

        var run = await RecipeExecutor.RunAsync(client, FrameFactsHypothesesEval(), "test-model");

        Assert.False(run.Ok);
        Assert.Equal("evaluation", run.FailedStepId);
        Assert.DoesNotContain(run.Graph.Edges, e => e.Kind == ReasoningEdges.Supports); // nothing assembled from the bad emission
    }

    // Guard #2: a weight outside [0,1] would corrupt the net-evidence sum — rejected
    // structurally by the evaluation schema.
    [Fact]
    public void EvaluationWeight_OutOfRange_RejectedStructurally()
    {
        static JsonNode Eval(string w) =>
            JsonNode.Parse($$"""{"weighings":[{"fact":"f0","hypothesis":"h0","stance":"supports","weight":{{w}}}]}""")!;

        Assert.False(JsonSchemaGen.Validate(ReasoningSchemas.Evaluation, Eval("1.5"), out _));
        Assert.False(JsonSchemaGen.Validate(ReasoningSchemas.Evaluation, Eval("-0.3"), out _));
        Assert.True(JsonSchemaGen.Validate(ReasoningSchemas.Evaluation, Eval("0.8"), out _));
    }

    // Guard #3 (surfaced by the live smoke): an out-of-vocabulary stance ("neutral") or
    // source_kind ("database") is rejected structurally — so the auto-fix loop re-prompts
    // instead of the executor crashing on an unknown enum value.
    [Fact]
    public void OutOfVocabulary_StanceAndSourceKind_RejectedStructurally()
    {
        Assert.False(JsonSchemaGen.Validate(ReasoningSchemas.Evaluation,
            JsonNode.Parse("""{"weighings":[{"fact":"f0","hypothesis":"h0","stance":"neutral","weight":0.5}]}"""), out _));
        Assert.False(JsonSchemaGen.Validate(ReasoningSchemas.Facts,
            JsonNode.Parse("""{"facts":[{"claim":"x","sourceKind":"database","sourceRef":"r"}]}"""), out _));

        Assert.True(JsonSchemaGen.Validate(ReasoningSchemas.Evaluation,
            JsonNode.Parse("""{"weighings":[{"fact":"f0","hypothesis":"h0","stance":"refutes","weight":0.5}]}"""), out _));
        Assert.True(JsonSchemaGen.Validate(ReasoningSchemas.Facts,
            JsonNode.Parse("""{"facts":[{"claim":"x","sourceKind":"doc","sourceRef":"r"}]}"""), out _));
    }
}
