using Plexus.Sidecar.Contract;

namespace Plexus.Sidecar.Tests;

// ADR-0002 R1 — reasoning invariants. Acceptance: a hand-built valid investigator
// subgraph yields zero errors. Negative controls: each invariant must fire on the
// graph that violates it. Backward-compat: a legacy (no-role) graph is a no-op.
public class ReasoningGraphValidatorTests
{
    // ── builders ────────────────────────────────────────────────────────────
    private static Node N(string id) => new() { Id = id, Role = "assistant", CreatedAt = "t" };

    private static Node R(string id, string role, string? srcKind = null, string? srcRef = null) => new()
    {
        Id = id,
        Role = "assistant",
        CreatedAt = "t",
        Reasoning = new ReasoningMeta { Role = role, SourceKind = srcKind, SourceRef = srcRef },
    };

    private static Edge E(string from, string to, string kind, double? weight = null) =>
        new() { From = from, To = to, Kind = kind, Weight = weight };

    private static Graph G(IEnumerable<Node> nodes, IEnumerable<Edge> edges) =>
        new() { Id = "g", Nodes = nodes.ToList(), Edges = edges.ToList() };

    // ── acceptance ──────────────────────────────────────────────────────────
    // frame → grounded facts → uncertainty → hypotheses (addresses + evidence) →
    // evaluation → conclusion (selects a net-positive hypothesis + cites). Zero errors.
    [Fact]
    public void ValidInvestigatorSubgraph_HasNoErrors()
    {
        var nodes = new[]
        {
            R("frame", ReasoningRoles.Frame),
            N("src1"), N("src2"), // RAG/API source nodes the facts ground to
            R("fact1", ReasoningRoles.Fact, FactSources.Doc, "catalog://1"),
            R("fact2", ReasoningRoles.Fact, FactSources.Api, "api://2"),
            R("u1", ReasoningRoles.Uncertainty),
            R("h1", ReasoningRoles.Hypothesis),
            R("h2", ReasoningRoles.Hypothesis),
            R("ev", ReasoningRoles.Evaluation),
            R("c", ReasoningRoles.Conclusion),
        };
        var edges = new[]
        {
            E("fact1", "src1", ReasoningEdges.Grounds),
            E("fact2", "src2", ReasoningEdges.Grounds),
            E("h1", "u1", ReasoningEdges.Addresses),
            E("h2", "u1", ReasoningEdges.Addresses),
            E("fact1", "h1", ReasoningEdges.Supports, 0.8),
            E("fact2", "h2", ReasoningEdges.Refutes, 0.3),
            E("c", "h1", ReasoningEdges.Selects),
            E("c", "fact1", ReasoningEdges.Cites),
        };

        var r = ReasoningGraphValidator.Validate(G(nodes, edges));

        Assert.False(r.HasErrors);
        Assert.False(r.HasFlags);
        Assert.Empty(r.Diagnostics);          // no warns either — fully sound
        Assert.Empty(r.OpenUncertainties);    // u1 is addressed
    }

    // ── negative control 1: provenance error (keys on source_ref) ───────────
    [Fact]
    public void Fact_NoSourceRef_RaisesProvenanceError()
    {
        var r = ReasoningGraphValidator.Validate(G(
            new[] { R("fact1", ReasoningRoles.Fact) }, // no SourceRef
            Array.Empty<Edge>()));

        var d = Assert.Single(r.Diagnostics);
        Assert.Equal(ReasoningSeverity.Error, d.Severity);
        Assert.Equal(ReasoningDiagnosticCodes.FactNoProvenance, d.Code);
        Assert.Equal("fact1", d.NodeId);
        Assert.True(r.HasErrors);
    }

    // R2-reload trap guard: provenance keys on the PERSISTED source_ref, never on the
    // grounds edge (which is not persisted — edges are rebuilt from parentId on load).
    // A fact with a valid source_ref and NO grounds edge must pass provenance, else
    // every fact reloaded in R2 would fail despite a perfect source_ref.
    [Fact]
    public void Fact_WithSourceRef_NoGroundsEdge_PassesProvenance()
    {
        var r = ReasoningGraphValidator.Validate(G(
            new[] { R("fact1", ReasoningRoles.Fact, FactSources.Doc, "catalog://1") },
            Array.Empty<Edge>())); // no grounds edge

        Assert.Empty(r.Diagnostics);
        Assert.False(r.HasErrors);
    }

    // ── negative control 2: net-negative selection flag ─────────────────────
    [Fact]
    public void ConclusionSelectsNetNegativeHypothesis_RaisesFlag()
    {
        // evidence comes from plain nodes (no fact role) so only the flag fires.
        var nodes = new[] { N("e1"), N("e2"), R("h", ReasoningRoles.Hypothesis), R("c", ReasoningRoles.Conclusion) };
        var edges = new[]
        {
            E("e1", "h", ReasoningEdges.Supports, 0.2),
            E("e2", "h", ReasoningEdges.Refutes, 0.9), // net = 0.2 − 0.9 = −0.7
            E("c", "h", ReasoningEdges.Selects),
        };

        var r = ReasoningGraphValidator.Validate(G(nodes, edges));

        var d = Assert.Single(r.Diagnostics);
        Assert.Equal(ReasoningSeverity.Flag, d.Severity);
        Assert.Equal(ReasoningDiagnosticCodes.ConclusionNetNegative, d.Code);
        Assert.Equal("c", d.NodeId);
        Assert.Equal("h", d.EdgeTo);
        Assert.True(r.HasFlags);
    }

    // Boundary: net == 0 is allowed (ADR-0002: net evidence must be ≥ 0). Only a
    // strictly negative net flags — a perfectly balanced hypothesis does not.
    [Fact]
    public void ConclusionSelectsNetZeroHypothesis_DoesNotFlag()
    {
        var nodes = new[] { N("e1"), N("e2"), R("h", ReasoningRoles.Hypothesis), R("c", ReasoningRoles.Conclusion) };
        var edges = new[]
        {
            E("e1", "h", ReasoningEdges.Supports, 0.5),
            E("e2", "h", ReasoningEdges.Refutes, 0.5), // net = 0.5 − 0.5 = 0 → allowed
            E("c", "h", ReasoningEdges.Selects),
        };

        var r = ReasoningGraphValidator.Validate(G(nodes, edges));

        Assert.False(r.HasFlags);
        Assert.Empty(r.Diagnostics);
    }

    // ── negative control 3: dangling hypothesis warn ────────────────────────
    [Fact]
    public void HypothesisWithNoAddressesNoEvidence_RaisesDanglingWarn()
    {
        var r = ReasoningGraphValidator.Validate(G(
            new[] { R("h", ReasoningRoles.Hypothesis) },
            Array.Empty<Edge>()));

        var d = Assert.Single(r.Diagnostics);
        Assert.Equal(ReasoningSeverity.Warn, d.Severity);
        Assert.Equal(ReasoningDiagnosticCodes.HypothesisDangling, d.Code);
        Assert.Equal("h", d.NodeId);
    }

    // ── negative control 4: surfaced (not dropped) uncertainty ──────────────
    [Fact]
    public void UnresolvedUncertainty_IsSurfacedNotDropped()
    {
        var r = ReasoningGraphValidator.Validate(G(
            new[] { R("u", ReasoningRoles.Uncertainty) },
            Array.Empty<Edge>()));

        Assert.Equal(new[] { "u" }, r.OpenUncertainties);
        Assert.Empty(r.Diagnostics); // surfaced, not an error/warn
    }

    // ── backward-compat: a legacy conversation graph is a clean no-op ───────
    [Fact]
    public void LegacyGraph_NoReasoningRoles_IsNoOp()
    {
        var nodes = new[] { new Node { Id = "u1", Role = "user", CreatedAt = "t" }, N("a1") };
        var edges = new[] { new Edge { From = "u1", To = "a1" } }; // structural, Kind == null

        var r = ReasoningGraphValidator.Validate(G(nodes, edges));

        Assert.Empty(r.Diagnostics);
        Assert.Empty(r.OpenUncertainties);
        Assert.False(r.HasErrors);
        Assert.False(r.HasFlags);
    }
}
