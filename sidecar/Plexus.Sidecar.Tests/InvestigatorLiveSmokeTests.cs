using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Model;
using Plexus.Sidecar.Services;
using Xunit.Abstractions;

namespace Plexus.Sidecar.Tests;

// ADR-0002 R2.0b LIVE SMOKE (opt-in; makes real API calls). The bet it tests is not
// "did it produce a graph" but "does a small real model + this scaffolding hold up":
// does the investigator recipe yield an R1-valid graph, and does auto-fix engage on
// REAL malformation? It instruments per step — structural vs referential retries,
// which steps exhaust — so the output tells you whether the tier holds, which steps to
// escalate by default, and which prompts/bounds to tune. Pass/fail says the wiring is
// connected; the counters say whether the thesis works.
//
// Run it:
//   PLEXUS_LIVE_SMOKE=1 ANTHROPIC_API_KEY=sk-… \
//     dotnet test --filter InvestigatorLiveSmoke -l "console;verbosity=detailed"
// Optional: PLEXUS_SMOKE_MODEL=claude-haiku-4-5 (default).
//
// Without PLEXUS_LIVE_SMOKE it no-ops (green), so the normal suite never calls the API.
public class InvestigatorLiveSmokeTests
{
    private readonly ITestOutputHelper _out;
    public InvestigatorLiveSmokeTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task InvestigatorLiveSmoke()
    {
        if (Environment.GetEnvironmentVariable("PLEXUS_LIVE_SMOKE") != "1")
        {
            Log("skipped — set PLEXUS_LIVE_SMOKE=1 (and ANTHROPIC_API_KEY) to run.");
            return;
        }

        var model = Environment.GetEnvironmentVariable("PLEXUS_SMOKE_MODEL") ?? "claude-haiku-4-5";
        var factory = new ChatClientFactory(new KeychainService(), ProvidersTests.IsolatedRegistry());
        using var client = factory.For("anthropic", model);

        const string caseText =
            "CASE: A nightly batch job silently skipped ~2,000 records last week; finance found a "
            + "reconciliation gap. The job exited with status 0 (no error). A config flag 'strictMode' "
            + "was switched off in a deploy two days earlier. The on-call engineer says throughput "
            + "looked normal. There is no alert on records-processed count.";

        Log($"model={model}");
        var run = await RecipeExecutor.RunAsync(client, Recipes.Investigator, model, context: caseText, maxAttempts: 4);

        int totalStructural = 0, totalReferential = 0;
        foreach (var s in run.Steps ?? Array.Empty<StepReport>())
        {
            Log($"step {s.StepId,-13} ok={s.Ok,-5} attempts={s.Attempts} structuralRetries={s.StructuralFailures} refRetries={s.ReferentialFailures}");
            totalStructural += s.StructuralFailures;
            totalReferential += s.ReferentialFailures;
        }
        Log($"TOTAL structuralRetries={totalStructural} refRetries={totalReferential} ok={run.Ok} failedStep={run.FailedStepId ?? "-"}");

        var v = ReasoningGraphValidator.Validate(run.Graph);
        Log($"graph nodes={run.Graph.Nodes.Count} edges={run.Graph.Edges.Count}");
        Log($"R1 errors={v.HasErrors} flags={v.HasFlags} diagnostics={v.Diagnostics.Count} openUncertainties={v.OpenUncertainties.Count}");
        foreach (var d in v.Diagnostics)
            Log($"  [{d.Severity}] {d.Code} {d.NodeId}: {d.Message}");

        Assert.True(run.Ok, run.Error);
        Assert.False(v.HasErrors); // the R1 acceptance bar, on real producer output
    }

    private void Log(string message)
    {
        _out.WriteLine(message);
        Console.WriteLine("[smoke] " + message); // also to stdout for `console` logger
    }
}
