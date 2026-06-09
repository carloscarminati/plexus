namespace Plexus.Sidecar.Contract;

// ADR-0002 R1 — reasoning invariants over graph STRUCTURE. This is a SEPARATE
// validation surface from ADR-0001's BlockCatalog (which checks a block spec is
// well-formed against the catalog). Different concern: here we check the reasoning
// graph is sound — facts are grounded, selected hypotheses aren't net-refuted,
// hypotheses aren't dangling, and open uncertainties are never silently dropped.
//
// It operates only on typed reasoning nodes (Node.Reasoning.Role set); a graph with
// no reasoning roles — i.e. an ordinary conversation — is a clean no-op.

public static class ReasoningSeverity
{
    public const string Error = "error"; // invalid: must be fixed (provenance)
    public const string Flag = "flag";   // contested: surface for escalate (net-negative selection)
    public const string Warn = "warn";   // suspicious: dangling hypothesis
}

// Diagnostic codes — stable identifiers for each invariant.
public static class ReasoningDiagnosticCodes
{
    public const string FactNoProvenance = "fact_no_provenance";
    public const string ConclusionNetNegative = "conclusion_net_negative";
    public const string HypothesisDangling = "hypothesis_dangling";
}

// One finding, tagged with severity, a stable code, and the offending node (and/or
// edge endpoints) so a UI/escalate surface can point at it.
public sealed record ReasoningDiagnostic(
    string Severity,
    string Code,
    string Message,
    string? NodeId = null,
    string? EdgeFrom = null,
    string? EdgeTo = null);

public sealed class ReasoningValidationResult
{
    public List<ReasoningDiagnostic> Diagnostics { get; } = new();

    // Uncertainty nodes with no resolving `addresses` — surfaced, never dropped, so a
    // deliverable can list them under "open questions / limitations" (ADR-0002 §4).
    public List<string> OpenUncertainties { get; } = new();

    public bool HasErrors => Diagnostics.Any(d => d.Severity == ReasoningSeverity.Error);
    public bool HasFlags => Diagnostics.Any(d => d.Severity == ReasoningSeverity.Flag);
}

public static class ReasoningGraphValidator
{
    public static ReasoningValidationResult Validate(Graph graph)
    {
        var result = new ReasoningValidationResult();
        var edges = graph.Edges;

        foreach (var node in graph.Nodes)
        {
            var role = node.Reasoning?.Role;
            if (role is null)
                continue; // legacy / untyped node — outside the reasoning contract

            switch (role)
            {
                case ReasoningRoles.Fact:
                    CheckFactProvenance(node, result);
                    break;
                case ReasoningRoles.Hypothesis:
                    CheckDanglingHypothesis(node, edges, result);
                    break;
                case ReasoningRoles.Uncertainty:
                    CollectOpenUncertainty(node, edges, result);
                    break;
                case ReasoningRoles.Conclusion:
                    CheckSelectedNetEvidence(node, edges, result);
                    break;
            }
        }

        return result;
    }

    // Invariant 1 (error): a fact's provenance keys on its PERSISTED source_ref, not
    // on a grounds edge. source_ref survives the SQLite round-trip (it's node metadata);
    // the grounds edge does NOT — edges are rebuilt from parentId on load, so a semantic
    // grounds edge is gone after a reload (deferred to R2/compose). Requiring it here
    // would fail every fact that round-trips through the DB even with a perfect
    // source_ref. So: a fact is grounded iff source_ref is non-empty; the grounds edge
    // is *derived* from source_ref at compose time, not asserted separately.
    private static void CheckFactProvenance(Node fact, ReasoningValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(fact.Reasoning?.SourceRef))
            result.Diagnostics.Add(new ReasoningDiagnostic(
                ReasoningSeverity.Error, ReasoningDiagnosticCodes.FactNoProvenance,
                $"Fact '{fact.Id}' has no source_ref.", NodeId: fact.Id));
    }

    // Invariant 2 (flag): a conclusion that `selects` a hypothesis whose net evidence
    // (Σ supports − Σ refutes, by magnitude) is negative is contested → flag it.
    private static void CheckSelectedNetEvidence(Node conclusion, List<Edge> edges, ReasoningValidationResult result)
    {
        foreach (var sel in edges.Where(e => e.Kind == ReasoningEdges.Selects && e.From == conclusion.Id))
        {
            var hypothesisId = sel.To;
            var net = 0.0;
            foreach (var e in edges.Where(e => e.To == hypothesisId))
            {
                if (e.Kind == ReasoningEdges.Supports) net += Math.Abs(e.Weight ?? 0);
                else if (e.Kind == ReasoningEdges.Refutes) net -= Math.Abs(e.Weight ?? 0);
            }

            if (net < 0)
                result.Diagnostics.Add(new ReasoningDiagnostic(
                    ReasoningSeverity.Flag, ReasoningDiagnosticCodes.ConclusionNetNegative,
                    $"Conclusion '{conclusion.Id}' selects hypothesis '{hypothesisId}' with net-negative evidence ({net:0.##}).",
                    NodeId: conclusion.Id, EdgeFrom: conclusion.Id, EdgeTo: hypothesisId));
        }
    }

    // Invariant 3 (warn): a hypothesis that addresses no uncertainty and has no
    // incoming supports/refutes is dangling — it's connected to nothing reasoned.
    private static void CheckDanglingHypothesis(Node hypothesis, List<Edge> edges, ReasoningValidationResult result)
    {
        var addresses = edges.Any(e => e.Kind == ReasoningEdges.Addresses && e.From == hypothesis.Id);
        var hasEvidence = edges.Any(e =>
            e.To == hypothesis.Id && (e.Kind == ReasoningEdges.Supports || e.Kind == ReasoningEdges.Refutes));

        if (!addresses && !hasEvidence)
            result.Diagnostics.Add(new ReasoningDiagnostic(
                ReasoningSeverity.Warn, ReasoningDiagnosticCodes.HypothesisDangling,
                $"Hypothesis '{hypothesis.Id}' addresses no uncertainty and has no supporting/refuting evidence.",
                NodeId: hypothesis.Id));
    }

    // Invariant 4 (must-not-drop): an uncertainty with no resolving `addresses` is
    // surfaced in the result so the deliverable can list it — never silently dropped.
    private static void CollectOpenUncertainty(Node uncertainty, List<Edge> edges, ReasoningValidationResult result)
    {
        var resolved = edges.Any(e => e.Kind == ReasoningEdges.Addresses && e.To == uncertainty.Id);
        if (!resolved)
            result.OpenUncertainties.Add(uncertainty.Id);
    }
}
