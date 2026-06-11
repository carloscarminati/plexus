using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Model;

namespace Plexus.Sidecar.Tests;

// ADR-0002 F4 — prose canonicalization. The model writes prose using its own ref tokens
// (f0/h1/u2); that map is discarded at persist, so the prose collides with the edge labels
// (which key on the persisted id, per F1). We rewrite the prose to persisted ids while the
// map is alive — one namespace — and the render maps id→label for prose AND edges.
public class ProseCanonicalizationTests
{
    // ── token rules: mapped → id, whole-word, free text + unmapped untouched ──
    [Fact]
    public void Canonicalize_RewritesMappedTokens_WordBoundary_LeavesRestIntact()
    {
        var node = new Node
        {
            Id = "n7",
            Role = "assistant",
            CreatedAt = "t",
            Raw = "h0 wins over h1; cite f0. Not h9, not config0, not info.",
            Blocks = new List<Block> { new MarkdownBlock { Text = "h0 wins over h1; cite f0." } },
        };
        var map = new Dictionary<string, string> { ["h0"] = "n5", ["h1"] = "n6", ["f0"] = "n1" };

        RecipeExecutor.CanonicalizeProse(new[] { node }, map);

        // mapped tokens rewritten; h9 (unmapped), config0 (not a whole-word key), info (free) intact.
        Assert.Equal("n5 wins over n6; cite n1. Not h9, not config0, not info.", node.Raw);
        // the markdown block is rewritten too, not just Raw.
        Assert.Equal("n5 wins over n6; cite n1.", ((MarkdownBlock)node.Blocks[0]).Text);
    }

    [Fact]
    public void Canonicalize_LongestFirst_DoesNotMatchShortRefInsideLong()
    {
        // f1 must not eat the "f1" inside f10 — word-boundary + longest-first guards it.
        var node = new Node { Id = "n0", Role = "assistant", CreatedAt = "t", Raw = "f1 and f10 differ", Blocks = new() };
        var map = new Dictionary<string, string> { ["f1"] = "nA", ["f10"] = "nB" };

        RecipeExecutor.CanonicalizeProse(new[] { node }, map);

        Assert.Equal("nA and nB differ", node.Raw);
    }

    // ── behavior-neutral: only prose changes; edges + R1 diagnostics untouched ──
    [Fact]
    public void Canonicalize_DoesNotTouchEdgesOrDiagnostics()
    {
        // A net-negative selection → R1 flags it. The flag depends on edges/weights, which
        // canon must not touch; the prose carries refs that DO get rewritten.
        var nodes = new List<Node>
        {
            new() { Id = "e1", Role = "assistant", CreatedAt = "t", Raw = "evidence" },
            new() { Id = "e2", Role = "assistant", CreatedAt = "t", Raw = "evidence" },
            new() { Id = "h", Role = "assistant", CreatedAt = "t", Raw = "the hypothesis", Reasoning = new ReasoningMeta { Role = ReasoningRoles.Hypothesis } },
            new() { Id = "c", Role = "assistant", CreatedAt = "t", Raw = "selecciono h0 pese a todo", Reasoning = new ReasoningMeta { Role = ReasoningRoles.Conclusion } },
        };
        var edges = new List<Edge>
        {
            new() { From = "e1", To = "h", Kind = ReasoningEdges.Supports, Weight = 0.2 },
            new() { From = "e2", To = "h", Kind = ReasoningEdges.Refutes, Weight = 0.9 }, // net negative
            new() { From = "c", To = "h", Kind = ReasoningEdges.Selects },
        };
        var graph = new Graph { Id = "g", Nodes = nodes, Edges = edges };

        var before = ReasoningGraphValidator.Validate(graph);
        var edgesBefore = edges.Select(EdgeKey).ToList();
        Assert.True(before.HasFlags); // there IS a diagnostic to preserve

        RecipeExecutor.CanonicalizeProse(graph.Nodes, new Dictionary<string, string> { ["h0"] = "h" });

        var after = ReasoningGraphValidator.Validate(graph);
        Assert.Equal(edgesBefore, edges.Select(EdgeKey).ToList());                  // edges identical
        Assert.Equal(before.HasFlags, after.HasFlags);                              // verdict identical
        Assert.Equal(DiagKey(before), DiagKey(after));
        Assert.Equal("selecciono h pese a todo", nodes[3].Raw);                     // only prose changed (h0 → h)

        static string EdgeKey(Edge e) => $"{e.From}|{e.To}|{e.Kind}|{e.Weight}";
        static IEnumerable<string> DiagKey(ReasoningValidationResult r) =>
            r.Diagnostics.Select(d => $"{d.Severity}:{d.Code}:{d.NodeId}").OrderBy(x => x);
    }

    // ── un-namespace (the gate), end-to-end through a real run ─────────────────
    // The conclusion summary references the model's refs (h0/f0/h1); after the run, that
    // prose must carry the PERSISTED ids — the same ids the selects/cites edges point at.
    private static string[] Script() => new[]
    {
        """{"question":"q"}""",
        """{"facts":[{"claim":"A","sourceKind":"doc","sourceRef":"r1"},{"claim":"B","sourceKind":"api","sourceRef":"r2"}]}""",
        """{"uncertainties":[{"question":"u?"}]}""",
        """{"hypotheses":[{"statement":"H-zero","addresses":["u0"]},{"statement":"H-one","addresses":["u0"]}]}""",
        """{"weighings":[{"fact":"f0","hypothesis":"h0","stance":"supports","weight":0.8}]}""",
        """{"selects":"h0","cites":["f0"],"summary":"Selecciono h0; sostenida por f0. La rival h1 queda descartada. (ver config0)"}""",
    };

    [Fact]
    public async Task Run_CanonicalizesConclusionProse_IntoOneNamespaceWithEdges()
    {
        var run = await RecipeExecutor.RunAsync(new ScriptedChatClient(Script()), Recipes.Investigator, "test-model");
        Assert.True(run.Ok);

        var concl = run.Graph.Nodes.Single(n => n.Reasoning?.Role == ReasoningRoles.Conclusion);
        var selectsTo = run.Graph.Edges.Single(e => e.Kind == ReasoningEdges.Selects && e.From == concl.Id).To;
        var citesTo = run.Graph.Edges.Single(e => e.Kind == ReasoningEdges.Cites && e.From == concl.Id).To;

        // The prose now names the SAME persisted ids the edges point at — one namespace.
        Assert.Contains(selectsTo, concl.Raw);
        Assert.Contains(citesTo, concl.Raw);
        // The raw model refs are gone (whole-word): no bare h0/f0 left in the prose.
        Assert.DoesNotContain("h0", concl.Raw.Split(' ', '.', ';', ',', '(', ')'));
        Assert.DoesNotContain("f0", concl.Raw.Split(' ', '.', ';', ',', '(', ')'));
        // The rival h1 (mentioned in prose but not selected/cited) was canonicalized too.
        var rivalId = run.Graph.Nodes.Where(n => n.Reasoning?.Role == ReasoningRoles.Hypothesis).Select(n => n.Id).Single(id => id != selectsTo);
        Assert.Contains(rivalId, concl.Raw);
        // A non-ref token that merely looks ref-ish is left intact.
        Assert.Contains("config0", concl.Raw);
    }
}
