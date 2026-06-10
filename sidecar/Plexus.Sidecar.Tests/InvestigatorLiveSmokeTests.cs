using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Model;
using Plexus.Sidecar.Services;
using Xunit.Abstractions;

namespace Plexus.Sidecar.Tests;

// ADR-0002 R2.0b LIVE SMOKE (opt-in; makes real API calls). The bet it tests is not
// "did it produce a graph" but "does a small real model + this scaffolding hold up":
// does the investigator recipe yield an R1-valid graph, and does auto-fix engage on
// REAL malformation? It instruments per step — structural vs referential retries, which
// steps exhaust — and runs N iterations so you read a DISTRIBUTION, not an anecdote: a
// single green run means the wiring is connected; a stable distribution means the thesis
// holds. The deepest signal is the R1 verdict (does it REASON soundly), not the retries
// (can it EMIT well-formed) — those are logged, not gated, because they're stochastic.
//
// Run it:
//   PLEXUS_LIVE_SMOKE=1 PLEXUS_SMOKE_ITERATIONS=5 ANTHROPIC_API_KEY=sk-… \
//     dotnet test --filter InvestigatorLiveSmoke -l "console;verbosity=detailed"
// Optional: PLEXUS_SMOKE_MODEL=claude-haiku-4-5 (default); PLEXUS_SMOKE_ITERATIONS=1.
//
// Without PLEXUS_LIVE_SMOKE it no-ops (green), so the normal suite never calls the API.
public class InvestigatorLiveSmokeTests
{
    private readonly ITestOutputHelper _out;
    public InvestigatorLiveSmokeTests(ITestOutputHelper output) => _out = output;

    // A representative case beats the toy: the counters only predict R2.2 if the case
    // resembles real complexity. Override with a real control investigation via
    // PLEXUS_SMOKE_CASE_FILE=<path> (or PLEXUS_SMOKE_CASE=<inline>); falls back to this toy.
    private const string CaseText =
        "CASE: A nightly batch job silently skipped ~2,000 records last week; finance found a "
        + "reconciliation gap. The job exited with status 0 (no error). A config flag 'strictMode' "
        + "was switched off in a deploy two days earlier. The on-call engineer says throughput "
        + "looked normal. There is no alert on records-processed count.";

    private static string ResolveCase()
    {
        var file = Environment.GetEnvironmentVariable("PLEXUS_SMOKE_CASE_FILE");
        if (!string.IsNullOrWhiteSpace(file) && File.Exists(file))
            return File.ReadAllText(file);
        var inline = Environment.GetEnvironmentVariable("PLEXUS_SMOKE_CASE");
        return string.IsNullOrWhiteSpace(inline) ? CaseText : inline;
    }

    [Fact]
    public async Task InvestigatorLiveSmoke()
    {
        if (Environment.GetEnvironmentVariable("PLEXUS_LIVE_SMOKE") != "1")
        {
            Log("skipped — set PLEXUS_LIVE_SMOKE=1 (and ANTHROPIC_API_KEY) to run.");
            return;
        }

        var model = Environment.GetEnvironmentVariable("PLEXUS_SMOKE_MODEL") ?? "claude-haiku-4-5";
        var escalateModel = Environment.GetEnvironmentVariable("PLEXUS_SMOKE_ESCALATE_MODEL"); // null = no escalation
        var grounded = Environment.GetEnvironmentVariable("PLEXUS_SMOKE_GROUND") == "1"; // ground facts in the mock corpus
        var iterations = int.TryParse(Environment.GetEnvironmentVariable("PLEXUS_SMOKE_ITERATIONS"), out var n) && n > 0 ? n : 1;
        var factory = new ChatClientFactory(new KeychainService(), ProvidersTests.IsolatedRegistry());
        using var client = factory.For("anthropic", model);
        var caseText = ResolveCase();
        var factSource = grounded ? new CuratedFactSource() : null;

        Log($"model={model} escalateModel={escalateModel ?? "(none)"} grounded={grounded} iterations={iterations} caseChars={caseText.Length}");
        Log($"case: {caseText.Replace('\n', ' ')[..Math.Min(140, caseText.Length)]}…");

        var stepStructural = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var stepReferential = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        int okRuns = 0, soundRuns = 0, openTotal = 0, escalatedRuns = 0;

        for (var i = 1; i <= iterations; i++)
        {
            RecipeRunResult run;
            try
            {
                run = await RecipeExecutor.RunAsync(client, Recipes.Investigator, model, context: caseText, escalateModelId: escalateModel, factSource: factSource, maxAttempts: 4);
            }
            catch (Exception ex)
            {
                // A run that throws is a real defect surfaced by real output — record it
                // and keep going so one bad run doesn't lose the whole distribution.
                Log($"run {i,2}/{iterations}: CRASHED — {ex.GetType().Name}: {ex.Message}");
                continue;
            }
            var v = ReasoningGraphValidator.Validate(run.Graph);
            // Soundness = no invariant VIOLATIONS (errors/flags/warns, all of which are
            // Diagnostics). Open uncertainties are NOT a violation — invariant #4 is
            // "must-not-drop": they're the surfaced "open questions / limitations" the
            // deliverable lists. A disciplined analyst leaves genuine gaps open instead
            // of fabricating resolution, so they're reported separately, not against sound.
            var sound = run.Ok && v.Diagnostics.Count == 0;
            if (run.Ok) okRuns++;
            if (sound) soundRuns++;
            openTotal += v.OpenUncertainties.Count;

            foreach (var s in run.Steps ?? Array.Empty<StepReport>())
            {
                Accumulate(stepStructural, s.StepId, s.StructuralFailures);
                Accumulate(stepReferential, s.StepId, s.ReferentialFailures);
            }

            var esc = run.EscalatedSteps is { Count: > 0 } es ? $" escalated=[{string.Join(",", es)}]" : "";
            if (run.EscalatedSteps is { Count: > 0 }) escalatedRuns++;
            var diag = v.Diagnostics.Count == 0 ? "" : " | " + string.Join("; ", v.Diagnostics.Select(d => $"{d.Severity}:{d.Code}"));
            Log($"run {i,2}/{iterations}: ok={run.Ok,-5} sound={sound,-5} "
                + $"R1[err={v.HasErrors} flag={v.HasFlags} warn={v.Diagnostics.Count} open={v.OpenUncertainties.Count}] "
                + $"failedStep={run.FailedStepId ?? "-"} nodes={run.Graph.Nodes.Count} edges={run.Graph.Edges.Count}{esc}{diag}");
        }

        Log("── per-step retry distribution (min/median/max across runs) ──");
        foreach (var step in Recipes.Investigator.Steps.Select(s => s.Id))
            Log($"  {step,-13} structural={Dist(stepStructural, step)}  referential={Dist(stepReferential, step)}");

        // The thesis line: wiring (okRuns) vs soundness (soundRuns). A run wiring through
        // proves the scaffolding; R1-sound runs are the actual bet — read soundRuns/N.
        Log($"── thesis: wired {okRuns}/{iterations} | R1-sound {soundRuns}/{iterations} (no invariant violations) "
            + $"| escalated {escalatedRuns}/{iterations} | open uncertainties surfaced {openTotal} total (limitations, not unsoundness) ──");

        // Gate only the wiring (the scaffolding must produce a graph at least once);
        // soundness is measured, not asserted (it's the stochastic thesis signal).
        Assert.True(okRuns >= 1, "no run produced a graph — the scaffolding is broken, not just the model");
    }

    private static void Accumulate(Dictionary<string, List<int>> map, string key, int value)
    {
        if (!map.TryGetValue(key, out var list))
            map[key] = list = new List<int>();
        list.Add(value);
    }

    private static string Dist(Dictionary<string, List<int>> map, string step)
    {
        if (!map.TryGetValue(step, out var xs) || xs.Count == 0)
            return "-/-/-";
        var sorted = xs.OrderBy(x => x).ToList();
        var median = sorted[sorted.Count / 2];
        return $"{sorted[0]}/{median}/{sorted[^1]}";
    }

    private void Log(string message)
    {
        _out.WriteLine(message);
        Console.WriteLine("[smoke] " + message); // also to stdout for the `console` logger
    }
}
