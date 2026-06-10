using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Model;

namespace Plexus.Sidecar.Tests;

// ADR-0002 R2.0b smoke instrumentation — the per-step counters the live smoke reads,
// verified deterministically with a scripted provider: structural retries vs
// referential retries are counted SEPARATELY (they say different things about the
// model tier), and the ref context is injected into ref-carrying steps' prompts.
public class RecipeTelemetryTests
{
    private static Recipe FrameFacts() => new()
    {
        Id = "t",
        Steps =
        {
            new() { Id = "frame", Role = ReasoningRoles.Frame, Prompt = "frame" },
            new() { Id = "facts", Role = ReasoningRoles.Fact, Array = true, MinItems = 1, Prompt = "facts" },
        },
    };

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
    private const string EvalGood = """{"weighings":[{"fact":"f0","hypothesis":"h0","stance":"supports","weight":0.8}]}""";
    private const string EvalBadRef = """{"weighings":[{"fact":"f0","hypothesis":"h7","stance":"supports","weight":0.8}]}""";

    // A shape failure is counted as STRUCTURAL (and not referential).
    [Fact]
    public async Task StructuralRetry_IsCountedStructurally()
    {
        var client = new ScriptedChatClient(
            Frame,
            """{"facts":[{"claim":"x"}]}""", // missing sourceKind/sourceRef → structural
            OneFact);

        var run = await RecipeExecutor.RunAsync(client, FrameFacts(), "test-model");

        var facts = Assert.Single(run.Steps!, s => s.StepId == "facts");
        Assert.Equal(2, facts.Attempts);
        Assert.Equal(1, facts.StructuralFailures);
        Assert.Equal(0, facts.ReferentialFailures);
    }

    // A bad ref is counted as REFERENTIAL (and not structural) — the emission's shape
    // was fine, the model just invented a ref.
    [Fact]
    public async Task ReferentialRetry_IsCountedReferentially()
    {
        var client = new ScriptedChatClient(Frame, OneFact, TwoHyps, EvalBadRef, EvalGood);

        var run = await RecipeExecutor.RunAsync(client, FrameFactsHypothesesEval(), "test-model");

        var eval = Assert.Single(run.Steps!, s => s.StepId == "evaluation");
        Assert.Equal(2, eval.Attempts);
        Assert.Equal(0, eval.StructuralFailures);
        Assert.Equal(1, eval.ReferentialFailures);
    }

    // The ref-carrying step's instruction lists the available refs so the model can
    // reference real ones instead of inventing them.
    [Fact]
    public async Task RefCarryingStep_InstructionListsAvailableRefs()
    {
        var client = new ScriptedChatClient(Frame, OneFact, TwoHyps, EvalGood);

        await RecipeExecutor.RunAsync(client, FrameFactsHypothesesEval(), "test-model");

        var evalInstruction = client.Instructions[3]; // frame=0, facts=1, hyps=2, evaluation=3
        Assert.Contains("f0", evalInstruction);
        Assert.Contains("h0", evalInstruction);
    }

    // The case/context is prepended to every step's instruction.
    [Fact]
    public async Task Context_IsPrependedToInstructions()
    {
        var client = new ScriptedChatClient(Frame, OneFact);

        await RecipeExecutor.RunAsync(client, FrameFacts(), "test-model", context: "CASE: the widget exploded.");

        Assert.All(client.Instructions, i => Assert.Contains("the widget exploded", i));
    }
}
