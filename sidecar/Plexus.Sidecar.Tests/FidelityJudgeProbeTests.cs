using Plexus.Sidecar.Model;
using Plexus.Sidecar.Services;
using Xunit.Abstractions;

namespace Plexus.Sidecar.Tests;

// ADR-0002 R2.2.0-fidelity LIVE PROBE (opt-in). Reads the REAL judge's discrimination on
// crafted (source, claim) pairs — the mechanism-vs-efficacy read the stub can't give.
// Centered on the failure that matters: OVER-CLAIMING from a correct-but-insufficient
// source (high overlap, plausible, the real laundering), not crude contradiction. Tests
// BOTH judge errors: false-negative (catch the over-claim) and false-positive (pass the
// faithful paraphrase clean — an over-strict judge breaks healthy runs).
//
//   PLEXUS_LIVE_SMOKE=1 ANTHROPIC_API_KEY=… PLEXUS_SMOKE_FIDELITY=claude-haiku-4-5 \
//     dotnet test --filter FidelityJudgeProbe -l "console;verbosity=detailed"
public class FidelityJudgeProbeTests
{
    private readonly ITestOutputHelper _out;
    public FidelityJudgeProbeTests(ITestOutputHelper output) => _out = output;

    private const string Wear = "La válvula A mostró desgaste a las 400 horas de operación.";
    private const string OverRev = "El motor fue operado por sobre el límite máximo de revoluciones.";
    private const string SealFixed = "El retén del diferencial fue reemplazado y el equipo quedó operativo.";

    private static readonly (string Source, string Claim, bool Expected, string Kind)[] Cases =
    {
        (Wear, "La válvula A mostró desgaste a las 400 horas de operación.", true,  "faithful-exact"),
        (Wear, "La válvula A presentó desgaste tras unas 400 horas de uso.", true,  "faithful-paraphrase"),  // false-positive guard
        (Wear, "La falla de la válvula A fue la causa raíz del incidente.",  false, "OVER-CLAIM (the one)"),  // supports wear, not root-cause
        (OverRev, "El operador ignoró deliberadamente las alarmas.",         false, "over-claim (intent)"),    // over-rev ≠ deliberate ignoring
        (SealFixed, "El retén nunca fue reemplazado.",                       false, "contradiction"),
        (Wear, "El camión tolva se quedó sin combustible.",                  false, "unrelated"),
    };

    [Fact]
    public async Task FidelityJudgeProbe()
    {
        if (Environment.GetEnvironmentVariable("PLEXUS_LIVE_SMOKE") != "1")
        {
            Log("skipped — set PLEXUS_LIVE_SMOKE=1 (and ANTHROPIC_API_KEY) to run.");
            return;
        }

        var model = Environment.GetEnvironmentVariable("PLEXUS_SMOKE_FIDELITY") ?? "claude-haiku-4-5";
        var factory = new ChatClientFactory(new KeychainService(), ProvidersTests.IsolatedRegistry());
        using var client = factory.For("anthropic", model);
        var judge = new LlmFidelityJudge(client, model);

        Log($"judge model={model}");
        int correct = 0;
        foreach (var (source, claim, expected, kind) in Cases)
        {
            var supported = await judge.IsSupportedAsync(claim, source);
            var ok = supported == expected;
            if (ok) correct++;
            Log($"  [{(ok ? "ok " : "MISS")}] {kind,-22} expected={(expected ? "YES" : "NO "),-3} got={(supported ? "YES" : "NO")}  claim=\"{claim}\"");
        }
        Log($"── judge accuracy {correct}/{Cases.Length} ──");

        // The two load-bearing cases: the faithful exact must pass (false-positive floor),
        // the over-claim must be caught (false-negative floor — this is what earns "fidelity").
        var faithfulExact = await judge.IsSupportedAsync(Cases[0].Claim, Cases[0].Source);
        var overClaim = await judge.IsSupportedAsync(Cases[2].Claim, Cases[2].Source);
        Assert.True(faithfulExact, "judge false-flagged a faithful exact claim (over-strict)");
        Assert.False(overClaim, "judge passed an over-claim from a correct-but-insufficient source (laundering slips)");
    }

    private void Log(string m)
    {
        _out.WriteLine(m);
        Console.WriteLine("[probe] " + m);
    }
}
