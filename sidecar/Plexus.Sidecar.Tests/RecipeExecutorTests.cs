using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Model;
using Plexus.Sidecar.Services;

namespace Plexus.Sidecar.Tests;

// ADR-0002 R2.0b — the recipe engine, end to end, with a scripted provider (no real
// model/case). The R1 ReasoningGraphValidator is the engine's oracle: a run is correct
// iff its produced graph validates. Two gates: acceptance (a full investigator run is
// sound) and the SEMANTIC control (R1 fires on real producer output, not a fixture).
public class RecipeExecutorTests
{
    // One valid emission per investigator step. Refs (f0/h0/u0…) match the executor's
    // per-role scheme, the way a real run would echo them into the model's prompt.
    private static readonly string[] ValidInvestigatorRun =
    {
        """{"question":"Why did control C fail?","scope":"Q1 audit"}""",
        """{"facts":[{"claim":"Log shows a bypass","sourceKind":"doc","sourceRef":"cat://1"},{"claim":"API returned 200","sourceKind":"api","sourceRef":"api://2"}]}""",
        """{"uncertainties":[{"question":"Was the bypass intentional?"}]}""",
        """{"hypotheses":[{"statement":"Misconfiguration","addresses":["u0"]},{"statement":"Deliberate override","addresses":["u0"]}]}""",
        """{"weighings":[{"fact":"f0","hypothesis":"h0","stance":"supports","weight":0.8},{"fact":"f1","hypothesis":"h1","stance":"refutes","weight":0.3}]}""",
        """{"selects":"h0","cites":["f0"]}""",
    };

    // ── acceptance ──────────────────────────────────────────────────────────
    [Fact]
    public async Task Investigator_ProducesGraph_ThatPassesR1()
    {
        var client = new ScriptedChatClient(ValidInvestigatorRun);

        var run = await RecipeExecutor.RunAsync(client, Recipes.Investigator, "test-model");

        Assert.True(run.Ok);
        Assert.Equal(8, run.Graph.Nodes.Count); // frame + 2 facts + 1 uncertainty + 2 hypotheses + evaluation + conclusion

        var v = ReasoningGraphValidator.Validate(run.Graph);
        Assert.False(v.HasErrors);
        Assert.False(v.HasFlags);
        Assert.Empty(v.Diagnostics);        // no warns either — a sound investigator subgraph
        Assert.Empty(v.OpenUncertainties);  // u0 is addressed

        // The weighing's magnitude rode onto the edge (the datum R2.1 will persist).
        Assert.Contains(run.Graph.Edges, e => e.Kind == ReasoningEdges.Supports && e.Weight == 0.8);
    }

    // ── semantic control: R1 fires on REAL producer output ──────────────────
    // A fact with an EMPTY source_ref is STRUCTURALLY valid (the field is present), so
    // the emitter accepts it — but R1 provenance flags it over the assembled graph.
    // This is the layer R2.0a deferred here: structure passes, semantics bite.
    [Fact]
    public async Task EmptySourceRef_PassesStructure_ButR1ProvenanceFlags()
    {
        var recipe = new Recipe
        {
            Id = "t",
            Steps =
            {
                new() { Id = "frame", Role = ReasoningRoles.Frame, Prompt = "frame" },
                new() { Id = "facts", Role = ReasoningRoles.Fact, Array = true, MinItems = 1, Prompt = "facts" },
            },
        };
        var client = new ScriptedChatClient(
            """{"question":"q"}""",
            """{"facts":[{"claim":"x","sourceKind":"doc","sourceRef":""}]}"""); // empty ref → structurally valid

        var run = await RecipeExecutor.RunAsync(client, recipe, "test-model");

        Assert.True(run.Ok); // structure passed — the emitter did NOT reject it
        var v = ReasoningGraphValidator.Validate(run.Graph);
        Assert.True(v.HasErrors);
        Assert.Contains(v.Diagnostics, d => d.Code == ReasoningDiagnosticCodes.FactNoProvenance);
    }

    // ── forward-note #1: array bounds come from the STEP CONFIG, not a constant ──
    [Fact]
    public async Task ArrayBounds_ComeFromStepConfig_NotAConstant()
    {
        var sevenHypotheses = "{\"hypotheses\":[" +
            string.Join(",", Enumerable.Range(0, 7).Select(i => $"{{\"statement\":\"h{i}\"}}")) + "]}";

        // maxItems = 8 (config) → 7 is accepted.
        var max8 = new Recipe { Id = "m8", Steps = { Step(maxItems: 8) } };
        var ok = await RecipeExecutor.RunAsync(new ScriptedChatClient(sevenHypotheses), max8, "test-model");
        Assert.True(ok.Ok);

        // maxItems = 6 (config) → the SAME 7 is rejected; auto-fix exhausts → explicit fail.
        var max6 = new Recipe { Id = "m6", Steps = { Step(maxItems: 6) } };
        var bad = await RecipeExecutor.RunAsync(
            new ScriptedChatClient(sevenHypotheses, sevenHypotheses, sevenHypotheses), max6, "test-model");
        Assert.False(bad.Ok);
        Assert.Equal("h", bad.FailedStepId);

        static RecipeStep Step(int maxItems) => new()
        { Id = "h", Role = ReasoningRoles.Hypothesis, Array = true, MinItems = 2, MaxItems = maxItems, Prompt = "h" };
    }

    // ── forward-note #2: the evaluation weight round-trips (R2.1-ready) ──────
    [Fact]
    public void EvaluationEmission_WeightRoundTrips()
    {
        var ev = new EvaluationEmission
        {
            Weighings = { new WeighingEmission { Fact = "f0", Hypothesis = "h0", Stance = "supports", Weight = 0.8 } },
        };

        var round = PlexusJson.Deserialize<EvaluationEmission>(PlexusJson.Serialize(ev))!;

        var w = Assert.Single(round.Weighings);
        Assert.Equal(0.8, w.Weight);
        Assert.Equal("supports", w.Stance);
        Assert.Equal("f0", w.Fact);
        Assert.Equal("h0", w.Hypothesis);
    }
}
