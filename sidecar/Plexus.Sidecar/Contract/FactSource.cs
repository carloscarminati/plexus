namespace Plexus.Sidecar.Contract;

// ADR-0002 R2.2 — the grounding seam. A retrieved source passage with a DURABLE id
// (a catalog/control node id or an API-call ref): a fact grounds on it by carrying
// that id as its source_ref, so provenance becomes VERIFIABLE (the ref resolves to a
// real source), not merely non-empty. The id is the node id of the source in the graph,
// so the grounds edge (fact → source) is derived by matching source_ref.
public sealed record SourcePassage(string Id, string Text, string Kind);

// What the facts step grounds against. R2.2.0 uses an in-memory curated mini-corpus
// (CuratedFactSource) to build + test the integration in isolation; R2.2.1 swaps in an
// MCP-backed source (the control/bowtie catalog + operational APIs). The executor
// consumes this interface, never a concrete corpus.
public interface IFactSource
{
    Task<IReadOnlyList<SourcePassage>> RetrieveAsync(string caseText, CancellationToken ct = default);
}

// Mock grounding source: returns its whole mini-corpus as the retrieved context (no real
// retrieval — that's the MCP catalog's job in R2.2.1). Isolated from any real catalog so
// a grounding failure is the engine's, not the data's.
public sealed class CuratedFactSource : IFactSource
{
    private readonly IReadOnlyList<SourcePassage> _corpus;

    public CuratedFactSource(IReadOnlyList<SourcePassage> corpus) => _corpus = corpus;
    public CuratedFactSource() : this(DefaultCorpus) { }

    public Task<IReadOnlyList<SourcePassage>> RetrieveAsync(string caseText, CancellationToken ct = default) =>
        Task.FromResult(_corpus);

    // A small control/bowtie-style corpus relevant to mining-equipment failures, so the
    // equipment cases ground to something. Stand-in for the real CMP/Collahuasi catalog.
    public static readonly IReadOnlyList<SourcePassage> DefaultCorpus = new SourcePassage[]
    {
        new("ctrl-lube-01", "Control de lubricación: pérdida de lubricación por sobre-revolución o fuga genera desgaste agresivo y giro de cojinetes/rodamientos.", FactSources.Doc),
        new("ctrl-seal-02", "Control de sellado de diferencial: un retén reinstalado que vuelve a fugar tras pocas horas indica daño en componentes internos (rodamiento de entrada).", FactSources.Doc),
        new("ctrl-coupling-03", "Control de acoplamiento eje-piñón: el corte de la chaveta causa giro lento del tambor y ausencia de frenado.", FactSources.Doc),
        new("ctrl-overspeed-04", "Control de exigencia operacional: mantener el motor sobre los límites máximos de revolución es una desviación operacional con consecuencia mecánica.", FactSources.Doc),
        new("api-maint-history", "Historial de mantenimiento (API activos): registros de intervenciones programadas, cambios de componente y fallas previas por equipo.", FactSources.Api),
        new("api-asset-risk", "Proceso gestión de análisis de riesgo de activos: evalúa impacto económico y costo-oportunidad de indisponibilidad del equipo.", FactSources.Api),
    };
}
