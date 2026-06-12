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
    public const string Warn = "warn";   // suspicious: dangling hypothesis, unweighed citation, off-argmax selection
}

// Diagnostic codes — stable identifiers for each invariant.
public static class ReasoningDiagnosticCodes
{
    public const string FactNoProvenance = "fact_no_provenance";
    public const string ConclusionNetNegative = "conclusion_net_negative";
    public const string HypothesisDangling = "hypothesis_dangling";
    public const string CitationNotWeighed = "citation_not_weighed";
    public const string SelectionNotBestWeighted = "selection_not_best_weighted";
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
                    CheckCitationsWeighed(node, edges, result);
                    CheckSelectionBestWeighted(node, graph.Nodes, edges, result);
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

    // The net evidence on a hypothesis: Σ supports − Σ refutes, by magnitude, over the
    // edges incident TO it. The SINGLE source of this computation — both the net-negative
    // flag (invariant 2) and the off-argmax warn (invariant 6) call it, so the two
    // invariants can never drift on how "net" or edge direction is read.
    private static double NetEvidence(string hypothesisId, List<Edge> edges)
    {
        var net = 0.0;
        foreach (var e in edges.Where(e => e.To == hypothesisId))
        {
            if (e.Kind == ReasoningEdges.Supports) net += Math.Abs(e.Weight ?? 0);
            else if (e.Kind == ReasoningEdges.Refutes) net -= Math.Abs(e.Weight ?? 0);
        }
        return net;
    }

    // Invariant 2 (flag): a conclusion that `selects` a hypothesis whose net evidence
    // (Σ supports − Σ refutes, by magnitude) is negative is contested → flag it.
    private static void CheckSelectedNetEvidence(Node conclusion, List<Edge> edges, ReasoningValidationResult result)
    {
        foreach (var sel in edges.Where(e => e.Kind == ReasoningEdges.Selects && e.From == conclusion.Id))
        {
            var hypothesisId = sel.To;
            var net = NetEvidence(hypothesisId, edges);

            if (net < 0)
                result.Diagnostics.Add(new ReasoningDiagnostic(
                    ReasoningSeverity.Flag, ReasoningDiagnosticCodes.ConclusionNetNegative,
                    // Invariant culture: a diagnostic is a serialized audit artifact — its number
                    // format must not depend on the server's locale ("0.7" not "0,7").
                    FormattableString.Invariant($"Conclusion '{conclusion.Id}' selects hypothesis '{hypothesisId}' with net-negative evidence ({net:0.##})."),
                    NodeId: conclusion.Id, EdgeFrom: conclusion.Id, EdgeTo: hypothesisId));
        }
    }

    // Invariant 6 (warn): the selected hypothesis is not the best-weighted one. The
    // selection is an independent model emission (constrained only to a non-net-negative
    // hypothesis by invariant 2); when it diverges from the weight argmax, the verdict no
    // longer "derives from the weights". This SURFACES that divergence — it does not force
    // the argmax (a follow-up, gated on how often this fires). Reuses NetEvidence, so it can
    // never disagree with the net-negative flag on what "net" means. Warn tier — does not
    // escalate to requires_review on its own. A small epsilon avoids firing on a float-noise
    // tie (a genuinely tied selection is a valid choice, not a divergence).
    private static void CheckSelectionBestWeighted(Node conclusion, List<Node> nodes, List<Edge> edges, ReasoningValidationResult result)
    {
        const double epsilon = 1e-9;
        var hypothesisIds = nodes.Where(n => n.Reasoning?.Role == ReasoningRoles.Hypothesis).Select(n => n.Id).ToList();
        if (hypothesisIds.Count == 0)
            return;

        foreach (var sel in edges.Where(e => e.Kind == ReasoningEdges.Selects && e.From == conclusion.Id))
        {
            var selectedId = sel.To;
            var selectedNet = NetEvidence(selectedId, edges);

            var bestId = hypothesisIds[0];
            var bestNet = double.NegativeInfinity;
            foreach (var h in hypothesisIds)
            {
                var net = NetEvidence(h, edges);
                if (net > bestNet) { bestNet = net; bestId = h; }
            }

            if (selectedNet < bestNet - epsilon)
                result.Diagnostics.Add(new ReasoningDiagnostic(
                    ReasoningSeverity.Warn, ReasoningDiagnosticCodes.SelectionNotBestWeighted,
                    // Invariant culture: a diagnostic is a serialized audit artifact, its number
                    // format must not depend on the server's locale (e.g. "0.5" not "0,5").
                    FormattableString.Invariant($"Selected hypothesis '{selectedId}' (net {selectedNet:0.##}) is not the best-weighted; '{bestId}' has net {bestNet:0.##}."),
                    NodeId: conclusion.Id, EdgeFrom: conclusion.Id, EdgeTo: selectedId));
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

    // Invariant 5 (warn): a fact the conclusion `cites` but that weighs on no hypothesis
    // (no outgoing supports/refutes edge) never participated in the evaluation — the
    // conclusion leans on it without the reasoning having tested it. This catches the
    // citation/selection coherence gap WITHOUT penalising legitimate breadth: a cited
    // fact that refutes a RIVAL (discriminating evidence) or refutes the SELECTED one
    // (acknowledged counter-evidence) DID weigh, so it passes — only an evaluation-less
    // citation warns. Warn tier (same as dangling-hypothesis): surfaced, never a hard fail.
    private static void CheckCitationsWeighed(Node conclusion, List<Edge> edges, ReasoningValidationResult result)
    {
        foreach (var cite in edges.Where(e => e.Kind == ReasoningEdges.Cites && e.From == conclusion.Id))
        {
            var factId = cite.To;
            var weighed = edges.Any(e =>
                e.From == factId && (e.Kind == ReasoningEdges.Supports || e.Kind == ReasoningEdges.Refutes));

            if (!weighed)
                result.Diagnostics.Add(new ReasoningDiagnostic(
                    ReasoningSeverity.Warn, ReasoningDiagnosticCodes.CitationNotWeighed,
                    $"Conclusion '{conclusion.Id}' cites fact '{factId}', which weighs on no hypothesis (no supports/refutes edge).",
                    NodeId: factId, EdgeFrom: conclusion.Id, EdgeTo: factId));
        }
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
