using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Model;

namespace Plexus.Sidecar.Tests;

// ADR-0002 R2.2.0-fidelity — claim ⊆ source. Grounding-by-resolution proves a fact cites
// a REAL source; fidelity proves the claim is SUPPORTED by it (blocking laundering: a real
// citation for an invented claim). Tested with a deterministic stub judge; the real
// entailment judge is an LLM (LlmFidelityJudge), plugged the same way.
public class ReasoningFidelityTests
{
    private static readonly IReadOnlyList<SourcePassage> Corpus = new SourcePassage[]
    {
        new("s1", "The engine was kept above maximum revolutions.", FactSources.Doc),
    };

    private static Recipe FrameFacts() => new()
    {
        Id = "t",
        Steps =
        {
            new() { Id = "frame", Role = ReasoningRoles.Frame, Prompt = "frame" },
            new() { Id = "facts", Role = ReasoningRoles.Fact, Array = true, MinItems = 1, Prompt = "facts" },
        },
    };

    private const string Frame = """{"question":"q"}""";
    private static string Fact(string claim) => $$"""{"facts":[{"claim":"{{claim}}","sourceKind":"doc","sourceRef":"s1"}]}""";

    // A claim the judge deems supported passes — and fidelity is actually exercised.
    [Fact]
    public async Task SupportedClaim_PassesFidelity()
    {
        var judge = new StubFidelityJudge((_, _) => true);
        var run = await RecipeExecutor.RunAsync(
            new ScriptedChatClient(Frame, Fact("engine over-revved")), FrameFacts(), "small",
            factSource: new CuratedFactSource(Corpus), fidelityJudge: judge);

        Assert.True(run.Ok);
        var facts = Assert.Single(run.Steps!, s => s.StepId == "facts");
        Assert.Equal(0, facts.ResolutionRetries + facts.FidelityRetries); // clean: neither fired
        Assert.True(judge.Calls >= 1); // the claim was actually judged, not waved through
    }

    // Negative control: a laundered claim (cites a real source but isn't supported by it)
    // is re-prompted, then corrected — never silently kept. This is the piece that turns
    // resolution into auditable grounding.
    [Fact]
    public async Task LaunderedClaim_IsReprompted_ThenSupported()
    {
        var judge = new StubFidelityJudge((claim, _) => !claim.Contains("laundered"));
        var run = await RecipeExecutor.RunAsync(
            new ScriptedChatClient(Frame, Fact("laundered invention"), Fact("engine over-revved")),
            FrameFacts(), "small", factSource: new CuratedFactSource(Corpus), fidelityJudge: judge);

        Assert.True(run.Ok);
        var facts = Assert.Single(run.Steps!, s => s.StepId == "facts");
        Assert.Equal(1, facts.FidelityRetries);   // an over-claim, attributed to fidelity
        Assert.Equal(0, facts.ResolutionRetries);  // not resolution (the ref resolved)
        Assert.Equal("engine over-revved", run.Graph.Nodes.Single(n => n.Reasoning?.Role == ReasoningRoles.Fact).Raw);
    }

    // Persistent laundering (the model never produces a supported claim) fails EXPLICITLY
    // at the facts step — an unsupportable claim is surfaced, never assembled.
    [Fact]
    public async Task PersistentLaundering_FailsExplicitly()
    {
        var judge = new StubFidelityJudge((_, _) => false);
        var run = await RecipeExecutor.RunAsync(
            new ScriptedChatClient(Frame, Fact("nope"), Fact("still nope")),
            FrameFacts(), "small", factSource: new CuratedFactSource(Corpus), fidelityJudge: judge, maxAttempts: 2);

        Assert.False(run.Ok);
        Assert.Equal("facts", run.FailedStepId);
        Assert.DoesNotContain(run.Graph.Nodes, n => n.Reasoning?.Role == ReasoningRoles.Fact);
    }

    private sealed class StubFidelityJudge : IFidelityJudge
    {
        private readonly Func<string, string, bool> _supported;
        public int Calls { get; private set; }
        public StubFidelityJudge(Func<string, string, bool> supported) => _supported = supported;
        public Task<bool> IsSupportedAsync(string claim, string sourceText, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(_supported(claim, sourceText));
        }
    }
}
