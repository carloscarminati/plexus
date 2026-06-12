using System.Globalization;
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

    // ── negative control 5: citation that weighs on nothing (warn) ──────────
    // A conclusion cites a fact that never weighed on any hypothesis (no supports/
    // refutes edge) → the conclusion leans on untested evidence → warn, naming the fact.
    [Fact]
    public void ConclusionCitesUnweighedFact_RaisesCitationWarn()
    {
        var nodes = new[] { R("f1", ReasoningRoles.Fact, FactSources.Doc, "doc://1"), R("c", ReasoningRoles.Conclusion) };
        var edges = new[] { E("c", "f1", ReasoningEdges.Cites) }; // f1 weighs on no hypothesis

        var r = ReasoningGraphValidator.Validate(G(nodes, edges));

        var d = Assert.Single(r.Diagnostics);
        Assert.Equal(ReasoningSeverity.Warn, d.Severity);
        Assert.Equal(ReasoningDiagnosticCodes.CitationNotWeighed, d.Code);
        Assert.Equal("f1", d.NodeId);       // names the fact, so the render is actionable
        Assert.False(r.HasErrors);
        Assert.False(r.HasFlags);           // warn tier — does not fail the step
    }

    // Acceptance (a) — discriminating evidence: a cited fact that REFUTES A RIVAL
    // hypothesis weighed on the evaluation → no warn. This is the difference against a
    // strict cite⊆support subset: legitimate breadth (ruling a rival out) must pass.
    [Fact]
    public void ConclusionCitesFactRefutingRival_DoesNotWarn()
    {
        var nodes = new[]
        {
            R("f1", ReasoningRoles.Fact, FactSources.Doc, "doc://1"),
            R("hr", ReasoningRoles.Hypothesis), // the rival
            R("c", ReasoningRoles.Conclusion),
        };
        var edges = new[]
        {
            E("f1", "hr", ReasoningEdges.Refutes, 0.7), // f1 weighed — it ruled hr out
            E("c", "f1", ReasoningEdges.Cites),
        };

        var r = ReasoningGraphValidator.Validate(G(nodes, edges));

        Assert.DoesNotContain(r.Diagnostics, d => d.Code == ReasoningDiagnosticCodes.CitationNotWeighed);
        Assert.Empty(r.Diagnostics); // hr is not dangling (has incoming refutes), f1 is grounded
    }

    // Acceptance (b) — acknowledged counter-evidence: a cited fact that REFUTES THE
    // SELECTED hypothesis (overruled by stronger support) weighed too → no warn. Honest
    // reasoning that cites what it argued past must not be flagged.
    [Fact]
    public void ConclusionCitesFactRefutingSelected_DoesNotWarn()
    {
        var nodes = new[]
        {
            R("f1", ReasoningRoles.Fact, FactSources.Doc, "doc://1"), // the counter-evidence
            R("f2", ReasoningRoles.Fact, FactSources.Api, "api://2"), // the stronger support
            R("hs", ReasoningRoles.Hypothesis), // the selected
            R("c", ReasoningRoles.Conclusion),
        };
        var edges = new[]
        {
            E("f2", "hs", ReasoningEdges.Supports, 0.8),
            E("f1", "hs", ReasoningEdges.Refutes, 0.3), // net = 0.5 ≥ 0 → not flagged
            E("c", "hs", ReasoningEdges.Selects),
            E("c", "f1", ReasoningEdges.Cites), // cites the counter-evidence it overruled
        };

        var r = ReasoningGraphValidator.Validate(G(nodes, edges));

        Assert.DoesNotContain(r.Diagnostics, d => d.Code == ReasoningDiagnosticCodes.CitationNotWeighed);
        Assert.Empty(r.Diagnostics); // f1 weighed (refutes hs), hs net-positive, both facts grounded
    }

    // ── invariant 6 (warn): selection diverges from the weight argmax ───────
    // The selection is an independent emission; when it isn't the best-weighted hypothesis,
    // surface it (don't force the argmax). Reuses the net-negative net computation.
    [Fact]
    public void SelectionNotArgmax_RaisesWarn_NamingSelectedAndBestWithNets()
    {
        // two hypotheses, both net-positive (no flag); the selection picks the lower-net one.
        var nodes = new[] { N("e1"), N("e2"), R("h1", ReasoningRoles.Hypothesis), R("h2", ReasoningRoles.Hypothesis), R("c", ReasoningRoles.Conclusion) };
        var edges = new[]
        {
            E("e1", "h1", ReasoningEdges.Supports, 0.5), // net h1 = 0.5
            E("e2", "h2", ReasoningEdges.Supports, 0.9), // net h2 = 0.9  ← argmax
            E("c", "h1", ReasoningEdges.Selects),         // selects the lower-weighted
        };

        var r = ReasoningGraphValidator.Validate(G(nodes, edges));

        var d = Assert.Single(r.Diagnostics);
        Assert.Equal(ReasoningSeverity.Warn, d.Severity);
        Assert.Equal(ReasoningDiagnosticCodes.SelectionNotBestWeighted, d.Code);
        Assert.Equal("c", d.NodeId);
        Assert.Equal("h1", d.EdgeTo);          // the selected hypothesis
        Assert.Contains("h1", d.Message);      // names both, by id (render relabels to H1/H2)
        Assert.Contains("h2", d.Message);
        Assert.Contains("0.5", d.Message);     // with their nets
        Assert.Contains("0.9", d.Message);
        Assert.False(r.HasFlags);              // warn tier — not a flag
    }

    [Fact]
    public void SelectionIsArgmax_NoWarn()
    {
        var nodes = new[] { N("e1"), N("e2"), R("h1", ReasoningRoles.Hypothesis), R("h2", ReasoningRoles.Hypothesis), R("c", ReasoningRoles.Conclusion) };
        var edges = new[]
        {
            E("e1", "h1", ReasoningEdges.Supports, 0.5),
            E("e2", "h2", ReasoningEdges.Supports, 0.9),
            E("c", "h2", ReasoningEdges.Selects), // selects the argmax
        };

        var r = ReasoningGraphValidator.Validate(G(nodes, edges));

        Assert.DoesNotContain(r.Diagnostics, d => d.Code == ReasoningDiagnosticCodes.SelectionNotBestWeighted);
        Assert.Empty(r.Diagnostics);
    }

    [Fact]
    public void SelectionTiedWithBest_NoWarn()
    {
        // both net 0.7 → the selected one is tied with the max → no warn (epsilon guard).
        var nodes = new[] { N("e1"), N("e2"), R("h1", ReasoningRoles.Hypothesis), R("h2", ReasoningRoles.Hypothesis), R("c", ReasoningRoles.Conclusion) };
        var edges = new[]
        {
            E("e1", "h1", ReasoningEdges.Supports, 0.7),
            E("e2", "h2", ReasoningEdges.Supports, 0.7),
            E("c", "h1", ReasoningEdges.Selects),
        };

        var r = ReasoningGraphValidator.Validate(G(nodes, edges));

        Assert.DoesNotContain(r.Diagnostics, d => d.Code == ReasoningDiagnosticCodes.SelectionNotBestWeighted);
    }

    // Additive: a net-negative AND off-argmax selection raises BOTH, independently — the
    // net-negative flag is byte-identical to before (same computation, reused), plus the warn.
    [Fact]
    public void NetNegativeAndOffArgmax_BothFire_Independently()
    {
        var nodes = new[] { N("e1"), N("e2"), N("e3"), R("h1", ReasoningRoles.Hypothesis), R("h2", ReasoningRoles.Hypothesis), R("c", ReasoningRoles.Conclusion) };
        var edges = new[]
        {
            E("e1", "h1", ReasoningEdges.Refutes, 0.9),  // net h1 = 0.2 − 0.9 = −0.7 (net-negative)
            E("e2", "h1", ReasoningEdges.Supports, 0.2),
            E("e3", "h2", ReasoningEdges.Supports, 0.5), // net h2 = 0.5  ← argmax
            E("c", "h1", ReasoningEdges.Selects),         // selects the net-negative, off-argmax one
        };

        var r = ReasoningGraphValidator.Validate(G(nodes, edges));

        Assert.Contains(r.Diagnostics, d => d.Code == ReasoningDiagnosticCodes.ConclusionNetNegative && d.NodeId == "c");
        Assert.Contains(r.Diagnostics, d => d.Code == ReasoningDiagnosticCodes.SelectionNotBestWeighted && d.NodeId == "c");
        Assert.True(r.HasFlags); // the flag still fires — the new warn is purely additive
    }

    // ── determinism guard: diagnostic numbers are culture-invariant ─────────
    // A diagnostic is part of the auditable record — its number format must not depend on
    // the server's locale. This forces a COMMA-decimal ambient culture (the failure mode of
    // ambient-culture formatting) and asserts the net renders with a "." separator. It must
    // FAIL against ambient `{net:0.##}` and pass once the message is InvariantCulture.
    [Fact]
    public void NetNegativeMessage_NumberFormat_IsCultureInvariant_UnderCommaLocale()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("es-AR"); // decimal separator = comma
            var nodes = new[] { N("e1"), N("e2"), R("h", ReasoningRoles.Hypothesis), R("c", ReasoningRoles.Conclusion) };
            var edges = new[]
            {
                E("e1", "h", ReasoningEdges.Supports, 0.2),
                E("e2", "h", ReasoningEdges.Refutes, 0.9), // net = 0.2 − 0.9 = −0.7
                E("c", "h", ReasoningEdges.Selects),
            };

            var d = Assert.Single(ReasoningGraphValidator.Validate(G(nodes, edges)).Diagnostics);

            Assert.Equal(ReasoningDiagnosticCodes.ConclusionNetNegative, d.Code);
            Assert.Contains("0.7", d.Message);       // invariant "." separator
            Assert.DoesNotContain("0,7", d.Message); // never the ambient comma
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ── R3 projection: per-hypothesis net via the SAME NetEvidence the verdict uses ──
    [Fact]
    public void HypothesisNets_ProjectsNetPerHypothesis_ViaNetEvidence()
    {
        // h1: +0.8 −0.3 = 0.5; h2: +0.4. Only hypotheses appear.
        var nodes = new[] { N("e1"), N("e2"), N("e3"), R("h1", ReasoningRoles.Hypothesis), R("h2", ReasoningRoles.Hypothesis) };
        var edges = new[]
        {
            E("e1", "h1", ReasoningEdges.Supports, 0.8),
            E("e2", "h1", ReasoningEdges.Refutes, 0.3),
            E("e3", "h2", ReasoningEdges.Supports, 0.4),
        };

        var nets = ReasoningGraphValidator.HypothesisNets(G(nodes, edges));

        Assert.Equal(2, nets.Count); // only hypotheses
        Assert.Equal(0.5, nets["h1"], 9);
        Assert.Equal(0.4, nets["h2"], 9);
    }

    // Additive: the projection is read-only — it raises no diagnostics and the net-negative
    // verdict is byte-identical to the same graph validated normally (shared NetEvidence).
    [Fact]
    public void HypothesisNets_IsAdditive_NetMatchesTheNetNegativeVerdict()
    {
        var nodes = new[] { N("e1"), N("e2"), R("h", ReasoningRoles.Hypothesis), R("c", ReasoningRoles.Conclusion) };
        var edges = new[]
        {
            E("e1", "h", ReasoningEdges.Supports, 0.2),
            E("e2", "h", ReasoningEdges.Refutes, 0.9), // net = −0.7
            E("c", "h", ReasoningEdges.Selects),
        };
        var g = G(nodes, edges);

        var nets = ReasoningGraphValidator.HypothesisNets(g);
        var d = Assert.Single(ReasoningGraphValidator.Validate(g).Diagnostics);

        Assert.Equal(-0.7, nets["h"], 9);                                // the projection's net…
        Assert.Equal(ReasoningDiagnosticCodes.ConclusionNetNegative, d.Code); // …matches the flag's net (-0.7), same source
        Assert.Contains("0.7", d.Message);
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
