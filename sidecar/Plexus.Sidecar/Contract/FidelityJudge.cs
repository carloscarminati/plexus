namespace Plexus.Sidecar.Contract;

// ADR-0002 R2.2.0-fidelity — judges whether a fact's CLAIM is supported by (entailed by)
// its cited source, not merely that the source_ref RESOLVES. This is the piece that turns
// grounding-by-resolution into auditable grounding: a model can cite a real passage and
// still launder an invented claim, and resolution alone never catches that. It's a
// separate, narrow check (the discipline-from-outside pattern, like the R1 invariants).
// Tests use a deterministic stub; real use plugs an LLM-judge (LlmFidelityJudge).
public interface IFidelityJudge
{
    Task<bool> IsSupportedAsync(string claim, string sourceText, CancellationToken ct = default);
}
