import { describe, it, expect } from "vitest";
import type { Graph, Node, Edge, ReasoningDiagnostic, ReasoningRole } from "./contract";
import { buildArgumentView, reduceReasoning, emptyReasoningSession, deriveReviewState, type ArgumentView } from "./reasoning-view";

// ── fixtures ────────────────────────────────────────────────────────────────
const rnode = (id: string, role: ReasoningRole, raw: string, src?: { kind: string; ref: string }): Node => ({
  id,
  parentId: null,
  role: "assistant",
  reasoning: { role, sourceKind: src?.kind as never, sourceRef: src?.ref },
  createdAt: id,
  blocks: [],
  raw,
});
const e = (from: string, to: string, kind: Edge["kind"], weight?: number): Edge => ({ from, to, kind, weight });

// A sound investigator graph: facts grounded, uncertainty addressed, conclusion selects
// a net-positive hypothesis and cites a fact.
function cleanGraph(): Graph {
  return {
    id: "g",
    nodes: [
      rnode("n0", "frame", "Why did the engine fail?"),
      rnode("n1", "fact", "Engine ran over its rev limit.", { kind: "doc", ref: "s1" }),
      rnode("n2", "fact", "Lubrication dropped and a bearing spun.", { kind: "api", ref: "s2" }),
      rnode("n3", "uncertainty", "Was the rev limit alarmed?"),
      rnode("n4", "hypothesis", "Operational over-demand"),
      rnode("n5", "hypothesis", "Lubrication system fault"),
      rnode("n6", "evaluation", "Evaluation"),
      rnode("n7", "conclusion", "Operational over-demand is the cause."),
      rnode("s1", "source", "Control: over-revving causes bearing wear."),
      rnode("s2", "source", "Maintenance API: lubrication pressure log."),
    ],
    edges: [
      e("n1", "s1", "grounds"),
      e("n2", "s2", "grounds"),
      e("n4", "n3", "addresses"),
      e("n5", "n3", "addresses"),
      e("n1", "n4", "supports", 0.8),
      e("n2", "n5", "refutes", 0.3),
      e("n7", "n4", "selects"),
      e("n7", "n1", "cites"),
    ],
  };
}

const flag: ReasoningDiagnostic = {
  severity: "flag",
  code: "conclusion_net_negative",
  message: "Conclusion selects a net-negative hypothesis.",
  nodeId: "n7",
};

// ── render: clean ───────────────────────────────────────────────────────────
describe("buildArgumentView — clean graph", () => {
  const view = buildArgumentView(cleanGraph(), [], []);

  it("renders the six role groups in reasoning order", () => {
    expect(view.frame?.text).toContain("Why did the engine fail");
    expect(view.facts).toHaveLength(2);
    expect(view.uncertainties).toHaveLength(1);
    expect(view.hypotheses).toHaveLength(2);
    expect(view.evaluation).toHaveLength(2);
    expect(view.conclusion).toBeDefined();
  });

  it("shows facts with their grounding (derived from source_ref)", () => {
    const f1 = view.facts[0];
    expect(f1.label).toBe("F1");
    expect(f1.sourceKind).toBe("doc");
    expect(f1.sourceText).toContain("over-revving causes bearing wear");
  });

  it("shows the evaluation per hypothesis (supports/refutes with weight)", () => {
    const h1 = view.evaluation.find((r) => r.hypothesisLabel === "H1")!;
    expect(h1.weighings).toEqual([{ factLabel: "F1", stance: "supports", weight: 0.8 }]);
  });

  it("shows the conclusion selection + citations, and the addressed uncertainty", () => {
    expect(view.conclusion!.selects).toBe("H1");
    expect(view.conclusion!.cites).toEqual(["F1"]);
    expect(view.uncertainties[0].open).toBe(false);
    expect(view.uncertainties[0].addressedBy).toEqual(["H1", "H2"]);
    expect(view.conclusion!.diagnostics).toHaveLength(0);
  });
});

// ── render: the flag (the important one) ────────────────────────────────────
describe("buildArgumentView — surfaces what R1 caught", () => {
  it("renders a net-negative flag visibly on the conclusion", () => {
    const view = buildArgumentView(cleanGraph(), [flag], []);
    expect(view.conclusion!.diagnostics).toHaveLength(1);
    expect(view.conclusion!.diagnostics[0].code).toBe("conclusion_net_negative");
  });

  it("a clean graph carries no flag on the conclusion", () => {
    const view = buildArgumentView(cleanGraph(), [], []);
    expect(view.conclusion!.diagnostics).toHaveLength(0);
  });

  it("surfaces an open uncertainty as open", () => {
    const view = buildArgumentView(cleanGraph(), [], ["n3"]);
    expect(view.uncertainties[0].open).toBe(true);
  });
});

// ── order-invariance: labels come from the persisted id, not array position ──
// A signable reference ("cites F1") must be stable across renderings; reordering the
// persisted nodes must renumber nothing and must not re-aim any cross-reference.
describe("buildArgumentView — references are id-keyed, not position-keyed", () => {
  const base = cleanGraph();
  const reordered: Graph = { ...base, nodes: [...base.nodes].reverse() };

  const labelsById = (v: ArgumentView) =>
    Object.fromEntries([...v.facts, ...v.hypotheses, ...v.uncertainties].map((x) => [x.id, x.label]));

  it("a reordered node array yields identical labels per node (the gate)", () => {
    const a = labelsById(buildArgumentView(base, [], []));
    const b = labelsById(buildArgumentView(reordered, [], []));
    expect(b).toEqual(a);
    // concretely: fact n1 is F1 and hypothesis n5 is H2, whatever the array order
    expect(b["n1"]).toBe("F1");
    expect(b["n5"]).toBe("H2");
  });

  it("cross-references resolve to the same nodes after a reorder", () => {
    const a = buildArgumentView(base, [], []);
    const b = buildArgumentView(reordered, [], []);
    expect(b.conclusion!.selects).toBe(a.conclusion!.selects); // "H1"
    expect(b.conclusion!.cites).toEqual(a.conclusion!.cites); // ["F1"]
    expect(b.evaluation).toEqual(a.evaluation);
    expect(b.uncertainties[0].addressedBy).toEqual(a.uncertainties[0].addressedBy); // ["H1","H2"]
  });

  it("cross-ref integrity: the conclusion cites the fact that bears that label", () => {
    const v = buildArgumentView(reordered, [], []);
    const citedLabel = v.conclusion!.cites[0]; // "F1"
    const cited = v.facts.find((f) => f.label === citedLabel)!;
    expect(cited.id).toBe("n1"); // the n7 --cites--> n1 edge, and n1 is the fact labelled F1
  });
});

// ── evaluation rationale (F2): the eval node's qualitative "why" ─────────────
describe("buildArgumentView — evaluation rationale", () => {
  it("surfaces the eval node's rationale, relabeled, beside the weighings", () => {
    const g = cleanGraph();
    const evalN = g.nodes.find((n) => n.reasoning?.role === "evaluation")!;
    evalN.raw = "n4 wins: n1 backs it strongly; rival n5 is weak."; // persisted ids → labels

    const v = buildArgumentView(g, [], []);

    expect(v.evaluationRationale).toBe("H1 wins: F1 backs it strongly; rival H2 is weak.");
    expect(v.evaluation.length).toBeGreaterThan(0); // the weighed breakdown is still there
  });

  it("treats the bare 'Evaluation' placeholder as no rationale", () => {
    const v = buildArgumentView(cleanGraph(), [], []); // fixture eval node raw === "Evaluation"
    expect(v.evaluationRationale).toBeUndefined();
  });
});

// ── diagnostic message relabel (Finding C render): ids → labels ──────────────
describe("buildArgumentView — diagnostic messages are relabeled", () => {
  it("relabels persisted ids in a diagnostic message to display labels, attached to the node", () => {
    const d: ReasoningDiagnostic = {
      severity: "warn",
      code: "selection_not_best_weighted",
      message: "Selected hypothesis 'n4' (net 0.5) is not the best-weighted; 'n5' has net 0.9.",
      nodeId: "n7", // the conclusion
    };

    const v = buildArgumentView(cleanGraph(), [d], []);

    // n4 → H1 (first hypothesis), n5 → H2 (second), surfaced on the conclusion
    expect(v.conclusion!.diagnostics[0].message).toBe(
      "Selected hypothesis 'H1' (net 0.5) is not the best-weighted; 'H2' has net 0.9.",
    );
    expect(v.diagnostics[0].message).toContain("'H1'");
    expect(v.conclusion!.diagnostics[0].code).toBe("selection_not_best_weighted");
  });
});

// ── prose relabel (F4): prose ids → the same labels the edges use ────────────
describe("buildArgumentView — prose and edges share one namespace", () => {
  it("relabels persisted-id tokens in prose to their display labels (the gate)", () => {
    const g = cleanGraph();
    const concl = g.nodes.find((n) => n.reasoning?.role === "conclusion")!;
    // canonicalized prose: persisted ids (n4 = a hypothesis, n1 = a fact, n5 = the rival).
    concl.raw = "n4 is the cause, supported by n1; rival n5 dropped; see n99.";

    const v = buildArgumentView(g, [], []);

    // prose uses F1/H1/H2 — the SAME labels the edges resolve to.
    expect(v.conclusion!.text).toBe("H1 is the cause, supported by F1; rival H2 dropped; see n99.");
    expect(v.conclusion!.selects).toBe("H1"); // edge n7→n4
    expect(v.conclusion!.cites).toEqual(["F1"]); // edge n7→n1
    // the prose label for the selected hypothesis matches the edge's label — no "h1/H2" split
    expect(v.conclusion!.text).toContain(v.conclusion!.selects!);
  });

  it("leaves an unmapped id token intact", () => {
    const g = cleanGraph();
    const concl = g.nodes.find((n) => n.reasoning?.role === "conclusion")!;
    concl.raw = "n99 has no label here.";
    const v = buildArgumentView(g, [], []);
    expect(v.conclusion!.text).toBe("n99 has no label here."); // untouched
  });
});

// ── drive round-trip (mocked WS as a pure event flow) ───────────────────────
describe("reduceReasoning — dev round-trip", () => {
  it("run → done → fetch → ready, then renders", () => {
    const afterDone = reduceReasoning({ ...emptyReasoningSession, status: "running" }, { type: "recipe_run_done", graphId: "g1" });
    expect(afterDone.send).toEqual({ type: "load_reasoning_graph", graphId: "g1" });
    expect(afterDone.session.status).toBe("loading");

    const afterGraph = reduceReasoning(afterDone.session, {
      type: "reasoning_graph",
      graph: cleanGraph(),
      diagnostics: [flag],
      openUncertainties: [],
    });
    expect(afterGraph.session.status).toBe("ready");

    const view = buildArgumentView(afterGraph.session.graph!, afterGraph.session.diagnostics, afterGraph.session.openUncertainties);
    expect(view.conclusion!.diagnostics[0].code).toBe("conclusion_net_negative");
  });
});

// ── review state derivation (Rx.2.1): pure function of (diagnostics, adjudication) ──
describe("deriveReviewState — review state is derived, not stored", () => {
  const warn: ReasoningDiagnostic = { severity: "warn", code: "citation_not_weighed", message: "advisory" };
  const error: ReasoningDiagnostic = { severity: "error", code: "fact_no_provenance", message: "bad" };
  const accept = { decision: "accept" as const, note: "accept despite the flag", reviewer: "carlos", timestamp: "2026-06-11T00:00:00Z" };
  const reject = { decision: "reject" as const, note: "the selection isn't justified", reviewer: "carlos", timestamp: "2026-06-11T00:00:00Z" };

  it("flag + no adjudication → requires_review", () => {
    expect(deriveReviewState([flag], null)).toBe("requires_review");
  });
  it("error tier also gates → requires_review", () => {
    expect(deriveReviewState([error], null)).toBe("requires_review");
  });
  it("flag + accept → accepted", () => {
    expect(deriveReviewState([flag], accept)).toBe("accepted");
  });
  it("flag + reject → rejected (distinct from accepted)", () => {
    expect(deriveReviewState([flag], reject)).toBe("rejected");
  });
  it("no flag → clean", () => {
    expect(deriveReviewState([], null)).toBe("clean");
  });
  it("no flag + reject → rejected (a human can reject a clean reasoning)", () => {
    expect(deriveReviewState([], reject)).toBe("rejected");
  });
  it("warn alone → clean (advisory, not review-gating)", () => {
    expect(deriveReviewState([warn], null)).toBe("clean");
  });

  it("transition: requires_review → accept → accepted, flag survives", () => {
    expect(deriveReviewState([flag], null)).toBe("requires_review");
    const after = adjudicated(accept);
    expect(deriveReviewState(after.diagnostics, after.adjudication)).toBe("accepted");
    expect(flagStillOnConclusion(after)).toBe(true);
  });

  it("transition: requires_review → reject → rejected (not green), flag survives", () => {
    expect(deriveReviewState([flag], null)).toBe("requires_review");
    const after = adjudicated(reject);
    expect(deriveReviewState(after.diagnostics, after.adjudication)).toBe("rejected");
    expect(flagStillOnConclusion(after)).toBe(true); // rejected = flagged AND rejected-with-reason, both visible
  });

  // Drive the real reducer flow: load a flagged graph, then land the adjudication beside it.
  function adjudicated(adjudication: { decision: "accept" | "reject"; note: string; reviewer: string; timestamp: string }) {
    const ready = reduceReasoning(
      { ...emptyReasoningSession, status: "loading" },
      { type: "reasoning_graph", graph: cleanGraph(), diagnostics: [flag], openUncertainties: [] },
    ).session;
    return reduceReasoning(ready, { type: "adjudication_saved", graphId: "g", adjudication }).session;
  }
  function flagStillOnConclusion(session: ReturnType<typeof adjudicated>) {
    const view = buildArgumentView(session.graph!, session.diagnostics, session.openUncertainties);
    return view.conclusion!.diagnostics[0]?.code === "conclusion_net_negative";
  }
});

// ── adjudication — additive metadata, never mutates the reasoning (Rx.2.0) ───
describe("reduceReasoning — adjudication is recorded beside the unchanged view", () => {
  const adj = { decision: "accept" as const, note: "accept despite the net-negative", reviewer: "carlos", timestamp: "2026-06-11T00:00:00Z" };

  it("adjudication_saved merges the decision into a ready session, untouched graph", () => {
    const ready = reduceReasoning(
      { ...emptyReasoningSession, status: "loading" },
      { type: "reasoning_graph", graph: cleanGraph(), diagnostics: [flag], openUncertainties: [] },
    ).session;
    expect(ready.adjudication).toBeNull();

    const after = reduceReasoning(ready, { type: "adjudication_saved", graphId: "g", adjudication: adj }).session;
    expect(after.adjudication).toEqual(adj);
    expect(after.graph).toBe(ready.graph); // same graph reference — reasoning untouched
    expect(after.diagnostics).toEqual(ready.diagnostics);
  });

  it("the flag SURVIVES an accept — adjudication sits beside it, never clears it", () => {
    const ready = reduceReasoning(
      { ...emptyReasoningSession, status: "loading" },
      { type: "reasoning_graph", graph: cleanGraph(), diagnostics: [flag], openUncertainties: [] },
    ).session;
    const after = reduceReasoning(ready, { type: "adjudication_saved", graphId: "g", adjudication: adj }).session;

    // the net-negative flag is still on the conclusion AFTER accepting
    const view = buildArgumentView(after.graph!, after.diagnostics, after.openUncertainties);
    expect(view.conclusion!.diagnostics[0].code).toBe("conclusion_net_negative");
    expect(after.adjudication!.decision).toBe("accept"); // flagged AND accepted-with-reason, both present
  });

  it("a reloaded graph carries its adjudication back (round-trip)", () => {
    const reloaded = reduceReasoning(
      { ...emptyReasoningSession, status: "loading" },
      { type: "reasoning_graph", graph: cleanGraph(), diagnostics: [], openUncertainties: [], adjudication: adj },
    ).session;
    expect(reloaded.adjudication).toEqual(adj);
  });
});
