namespace Plexus.Sidecar.Contract;

// ADR-0002 R0 — graph-layer reasoning metadata. This is DISTINCT from the ADR-0001
// render catalog: a node's *content* still renders via the block catalog; its
// reasoning *role* is graph-layer metadata that lives here. All fields are additive
// and optional — a node/edge with no reasoning metadata is a legacy turn and behaves
// exactly as before. Values are lowercase strings (matching the codebase's existing
// discriminator convention, e.g. block "type" and node "role"); they are not C#
// enums because the sidecar's JSON has no string-enum converter (enums would
// serialize as ints and break the camelCase string contract + round-trip).

// Node reasoning roles (the typed vocabulary from ADR-0002 §Design).
public static class ReasoningRoles
{
    public const string Frame = "frame";           // the case: question, scope — subgraph root
    public const string Fact = "fact";             // atomic, provenance-typed fact
    public const string Uncertainty = "uncertainty"; // gap / unknown / low-confidence flag
    public const string Hypothesis = "hypothesis"; // candidate explanation
    public const string Evaluation = "evaluation"; // the weighing of facts against hypotheses
    public const string Conclusion = "conclusion"; // selected synthesis
}

// Provenance kind for a `fact` node's source (ADR-0002: source_kind).
public static class FactSources
{
    public const string Doc = "doc";     // RAG / document
    public const string Api = "api";     // operational API call
    public const string Given = "given"; // stipulated by the frame / expert
}

// Typed, directional reasoning edges (ADR-0002 §Edge vocabulary). A semantic edge
// coexists with the structural branch/merge edges already derived from parentId;
// it is distinguished by a non-null Kind. A null Kind = a legacy structural edge.
public static class ReasoningEdges
{
    public const string Grounds = "grounds";     // fact → source (provenance)
    public const string Addresses = "addresses"; // hypothesis → uncertainty
    public const string Supports = "supports";   // fact → hypothesis (carries weight)
    public const string Refutes = "refutes";     // fact → hypothesis (carries weight)
    public const string Selects = "selects";     // conclusion → hypothesis
    public const string Cites = "cites";         // conclusion → fact
}

// Per-node reasoning metadata. Absent on legacy/conversation nodes; present on the
// typed reasoning nodes a recipe (R2) produces. Source* are meaningful only for a
// `fact` (they carry its provenance).
public sealed class ReasoningMeta
{
    public string? Role { get; set; }        // one of ReasoningRoles; null = untyped/legacy
    public string? SourceKind { get; set; }  // fact only: one of FactSources
    public string? SourceRef { get; set; }   // fact only: the provenance reference (URI/string)
}
