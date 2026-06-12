// ADR-0002 Rx-next — pure view-model logic for the structured-argument view. Kept out
// of the React component so it's testable: (graph + server-computed R1 diagnostics) →
// a flat model the view renders. The diagnostics are the system's own (the C# validator),
// so a flagged conclusion can never be presented as clean.
import type { Adjudication, ClientEvent, Edge, Graph, ReasoningDiagnostic, ServerEvent } from "./contract";

export interface FactItem {
  id: string;
  label: string;
  text: string;
  sourceKind?: string;
  sourceRef?: string;
  sourceText?: string; // the cited source node's text (grounds derived from source_ref)
  diagnostics: ReasoningDiagnostic[];
}
export interface UncertaintyItem {
  id: string;
  label: string;
  text: string;
  open: boolean; // unaddressed — surfaced, must not be dropped
  addressedBy: string[]; // hypothesis labels
  diagnostics: ReasoningDiagnostic[];
}
export interface HypothesisItem {
  id: string;
  label: string;
  text: string;
  addresses: string[]; // uncertainty labels
  diagnostics: ReasoningDiagnostic[];
}
export interface Weighing {
  factLabel: string;
  stance: "supports" | "refutes";
  weight?: number;
}
export interface EvaluationRow {
  hypothesisLabel: string;
  weighings: Weighing[];
}
export interface ConclusionItem {
  id: string;
  text: string;
  selects?: string; // hypothesis label
  cites: string[]; // fact labels
  diagnostics: ReasoningDiagnostic[];
}
// Rx B — the evaluation rationale is the MODEL's narrative. The GMN001 read showed it can
// contradict the verdict (crown a different hypothesis than the selection), invent weights,
// and miscite edges. So it is exposed as a NOTE with an epistemic-status label — never as
// the authoritative "why" (that is the weighted breakdown + the selection). The render shows
// it subordinate to the breakdown so a reviewer reads it as a model note to cross-check.
export const EVALUATION_RATIONALE_NOTE =
  "Model note — not derived from or verified against the weights; cross-check against the breakdown above.";

export interface EvaluationNote {
  label: string; // epistemic status (EVALUATION_RATIONALE_NOTE)
  text: string; // the model's rationale, relabeled
}

export interface ArgumentView {
  frame?: { id: string; text: string };
  facts: FactItem[];
  uncertainties: UncertaintyItem[];
  hypotheses: HypothesisItem[];
  evaluation: EvaluationRow[];
  evaluationNote?: EvaluationNote; // F2 + B — the model's rationale, as an unverified note (not the verdict)
  conclusion?: ConclusionItem;
  diagnostics: ReasoningDiagnostic[]; // all, for a summary banner
}

const edgesOfKind = (edges: Edge[], kind: string) => edges.filter((e) => e.kind === kind);

// Total order over a role's nodes by their persisted id (n0, n1, …), independent of the
// array's ordering — so a label is a function of the id, never of array position. Ids
// share a textual prefix + numeric suffix; compare the suffix numerically (n2 < n10),
// falling back to lexical for any id that doesn't fit that shape.
function stableIdCompare(a: string, b: string): number {
  const ma = /^(\D*)(\d+)$/.exec(a);
  const mb = /^(\D*)(\d+)$/.exec(b);
  if (ma && mb && ma[1] === mb[1]) return Number(ma[2]) - Number(mb[2]);
  return a < b ? -1 : a > b ? 1 : 0;
}

// A role's nodes in stable-id order (id-rank, never array position) — so labels and row
// order are a deterministic function of the persisted ids (a reorder renumbers nothing).
const roleNodes = (graph: Graph, role: string) =>
  graph.nodes.filter((n) => n.reasoning?.role === role).sort((x, y) => stableIdCompare(x.id, y.id));

// The id→display-label map (F1, H1, U1), SHARED by the argument view, the prose relabel, and
// the evaluation matrix — so F1 means the same fact everywhere (the cross-check lines up).
function buildLabelMap(graph: Graph): Map<string, string> {
  const label = new Map<string, string>();
  const group = (role: string, prefix: string) =>
    roleNodes(graph, role).forEach((n, i) => label.set(n.id, `${prefix}${i + 1}`));
  group("fact", "F");
  group("hypothesis", "H");
  group("uncertainty", "U");
  return label;
}

export function buildArgumentView(
  graph: Graph,
  diagnostics: ReasoningDiagnostic[],
  openUncertainties: string[],
): ArgumentView {
  const nodeById = new Map(graph.nodes.map((n) => [n.id, n]));
  const byRole = (role: string) => roleNodes(graph, role);

  // Display labels (F1, H1, U1) by id-rank — the SAME map the matrix uses (shared helper).
  const label = buildLabelMap(graph);
  const lbl = (id: string) => label.get(id) ?? id;

  // F4: prose carries persisted node ids (canonicalized at persist); rewrite each whole-word
  // id token to its display label, reusing the SAME id→label map the edges use — so prose
  // and edges show one label per node. An id with no label (or any other token) is left as-is.
  const proseRe = label.size
    ? new RegExp("\\b(?:" + [...label.keys()].map((k) => k.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")).join("|") + ")\\b", "g")
    : null;
  const relabel = (text: string) => (proseRe ? text.replace(proseRe, (t) => label.get(t) ?? t) : text);

  // Diagnostic messages embed persisted ids (e.g. selection_not_best_weighted names the
  // selected vs best-weighted hypothesis); relabel them to display labels with the SAME map,
  // so the warn reads "Selected 'H1' … 'H2' has net …", consistent with the edges.
  const relabeledDiagnostics = diagnostics.map((d) => ({ ...d, message: relabel(d.message) }));
  const diagFor = (id: string) => relabeledDiagnostics.filter((d) => d.nodeId === id);

  const frameNode = byRole("frame")[0];

  const facts: FactItem[] = byRole("fact").map((n) => {
    const grounds = graph.edges.find((e) => e.kind === "grounds" && e.from === n.id);
    return {
      id: n.id,
      label: lbl(n.id),
      text: relabel(n.raw),
      sourceKind: n.reasoning?.sourceKind,
      sourceRef: n.reasoning?.sourceRef,
      sourceText: grounds ? nodeById.get(grounds.to)?.raw : undefined,
      diagnostics: diagFor(n.id),
    };
  });

  const uncertainties: UncertaintyItem[] = byRole("uncertainty").map((n) => ({
    id: n.id,
    label: lbl(n.id),
    text: relabel(n.raw),
    open: openUncertainties.includes(n.id),
    addressedBy: edgesOfKind(graph.edges, "addresses").filter((e) => e.to === n.id).map((e) => lbl(e.from)),
    diagnostics: diagFor(n.id),
  }));

  const hypotheses: HypothesisItem[] = byRole("hypothesis").map((n) => ({
    id: n.id,
    label: lbl(n.id),
    text: relabel(n.raw),
    addresses: edgesOfKind(graph.edges, "addresses").filter((e) => e.from === n.id).map((e) => lbl(e.to)),
    diagnostics: diagFor(n.id),
  }));

  // Evaluation: per hypothesis, the facts weighed for/against it.
  const evaluation: EvaluationRow[] = byRole("hypothesis")
    .map((h) => ({
      hypothesisLabel: lbl(h.id),
      weighings: graph.edges
        .filter((e) => (e.kind === "supports" || e.kind === "refutes") && e.to === h.id)
        .map((e) => ({ factLabel: lbl(e.from), stance: e.kind as "supports" | "refutes", weight: e.weight })),
    }))
    .filter((row) => row.weighings.length > 0);

  // F2: the evaluation node's content is the rationale (was a bare "Evaluation" placeholder
  // pre-F2). B: expose it as an unverified model note, relabeled; skip the placeholder.
  const evalNode = byRole("evaluation")[0];
  const evaluationNote: EvaluationNote | undefined =
    evalNode && evalNode.raw && evalNode.raw !== "Evaluation"
      ? { label: EVALUATION_RATIONALE_NOTE, text: relabel(evalNode.raw) }
      : undefined;

  const conclNode = byRole("conclusion")[0];
  const conclusion: ConclusionItem | undefined = conclNode && {
    id: conclNode.id,
    text: relabel(conclNode.raw),
    selects: graph.edges.filter((e) => e.kind === "selects" && e.from === conclNode.id).map((e) => lbl(e.to))[0],
    cites: edgesOfKind(graph.edges, "cites").filter((e) => e.from === conclNode.id).map((e) => lbl(e.to)),
    diagnostics: diagFor(conclNode.id),
  };

  return {
    frame: frameNode && { id: frameNode.id, text: relabel(frameNode.raw) },
    facts,
    uncertainties,
    hypotheses,
    evaluation,
    evaluationNote,
    conclusion,
    diagnostics: relabeledDiagnostics,
  } satisfies ArgumentView;
}

// ── evaluation matrix (R3, ACH) — the faithful structural "why" ──────────────
// A facts × hypotheses projection of the SAME edges + weights the validator reads. Cells are
// signed weights (supports +, refutes −) straight from the edges (raw data, no computation);
// column nets come from the SERVER (hypothesisNets, via NetEvidence) — never recomputed in TS,
// so the matrix can't drift from the verdict / the net-negative flag / the off-argmax warn.
export interface MatrixHypCol {
  id: string;
  label: string; // H1, H2…
  net: number; // server net (single source of truth)
  selected: boolean; // the conclusion's selected hypothesis
  bestWeighted: boolean; // argmax(net)
}
export interface MatrixFactRow {
  id: string;
  label: string; // F1, F2…
  cells: (number | null)[]; // signed weight per hypCol (index-aligned); null = no edge
}
export interface EvaluationMatrix {
  hypCols: MatrixHypCol[];
  factRows: MatrixFactRow[];
  selectedId?: string;
  bestWeightedId?: string;
  divergent: boolean; // selected ≠ best-weighted (the same fact C's warn reports)
}

export function buildEvaluationMatrix(
  graph: Graph,
  hypothesisNets: Record<string, number>,
  diagnostics: ReasoningDiagnostic[],
): EvaluationMatrix {
  const label = buildLabelMap(graph);
  const lbl = (id: string) => label.get(id) ?? id;

  const hyps = roleNodes(graph, "hypothesis");
  const selectedId = graph.edges.find((e) => e.kind === "selects")?.to;

  // best-weighted = argmax of the SERVER nets (read, not recomputed).
  let bestWeightedId: string | undefined;
  let bestNet = -Infinity;
  for (const h of hyps) {
    const net = hypothesisNets[h.id] ?? 0;
    if (net > bestNet) {
      bestNet = net;
      bestWeightedId = h.id;
    }
  }

  const hypCols: MatrixHypCol[] = hyps.map((h) => ({
    id: h.id,
    label: lbl(h.id),
    net: hypothesisNets[h.id] ?? 0,
    selected: h.id === selectedId,
    bestWeighted: h.id === bestWeightedId,
  }));

  // signed weight of a fact→hyp weighing edge (supports +, refutes −), or null if none.
  const signedCell = (factId: string, hypId: string): number | null => {
    const e = graph.edges.find(
      (x) => x.from === factId && x.to === hypId && (x.kind === "supports" || x.kind === "refutes"),
    );
    if (!e) return null;
    const w = e.weight ?? 0;
    return e.kind === "supports" ? w : -w;
  };

  // rows = facts with ≥1 supports/refutes edge to some hypothesis.
  const factRows: MatrixFactRow[] = roleNodes(graph, "fact")
    .filter((f) => graph.edges.some((e) => e.from === f.id && (e.kind === "supports" || e.kind === "refutes")))
    .map((f) => ({ id: f.id, label: lbl(f.id), cells: hyps.map((h) => signedCell(f.id, h.id)) }));

  // Divergence IS C's verdict (the selection_not_best_weighted warn), not a TS recompute —
  // so the matrix mark can never drift from the warn at the ε boundary (single-sourced, like
  // the nets). bestWeightedId stays a display highlight of the argmax over the shown nets.
  const divergent = diagnostics.some((d) => d.code === "selection_not_best_weighted");

  return { hypCols, factRows, selectedId, bestWeightedId, divergent };
}

// ── review state (Rx.2.1) — derived, no new persisted state ──────────────────
// A flagged PERSISTED graph already implies escalate-exhausted (the run finished and the
// flag stuck), so "needs a human" is a pure function of what already travels with the
// graph: the R1 diagnostics (Rx-next.0) and the adjudication (Rx.2.0). An adjudication
// resolves it — but accept and reject are DISTINCT outcomes ("a reviewer rejected this" is
// not "OK"), so the decision lives in the STATE, not just the banner. An unresolved
// flag/error demands a human; warns alone are advisory.
export type ReviewState = "clean" | "requires_review" | "accepted" | "rejected";

export function deriveReviewState(
  diagnostics: ReasoningDiagnostic[],
  adjudication: Adjudication | null | undefined,
): ReviewState {
  // A human decision (on a flagged OR a clean graph) is the resolved outcome — accept and
  // reject are separate states; the flag itself stays visible for audit either way.
  if (adjudication) return adjudication.decision === "reject" ? "rejected" : "accepted";
  const flagged = diagnostics.some((d) => d.severity === "flag" || d.severity === "error");
  return flagged ? "requires_review" : "clean"; // warns alone are advisory, not review-gating
}

// ── dev round-trip flow (pure; the hook + a mock WS drive the same reducer) ──────────
export interface ReasoningSession {
  status: "idle" | "running" | "loading" | "ready" | "error";
  graph: Graph | null;
  diagnostics: ReasoningDiagnostic[];
  openUncertainties: string[];
  adjudication: Adjudication | null; // ADR-0002 Rx.2.0 — the human decision, beside (never folded into) the reasoning
  hypothesisNets: Record<string, number>; // R3 — server-computed per-hypothesis net, the matrix's column totals
  error?: string;
}

export const emptyReasoningSession: ReasoningSession = {
  status: "idle",
  graph: null,
  diagnostics: [],
  openUncertainties: [],
  adjudication: null,
  hypothesisNets: {},
};

// Process a server event for the reasoning dev flow; returns the next session and an
// optional client event to send (run → done → load → graph).
export function reduceReasoning(
  session: ReasoningSession,
  event: ServerEvent,
): { session: ReasoningSession; send?: ClientEvent } {
  switch (event.type) {
    case "recipe_run_done":
      return {
        session: { ...session, status: "loading" },
        send: { type: "load_reasoning_graph", graphId: event.graphId },
      };
    case "reasoning_graph":
      return {
        session: {
          status: "ready",
          graph: event.graph,
          diagnostics: event.diagnostics,
          openUncertainties: event.openUncertainties,
          adjudication: event.adjudication ?? null,
          hypothesisNets: event.hypothesisNets,
        },
      };
    // Additive: the adjudication is merged beside the unchanged argument view — the graph
    // and its R1 diagnostics are deliberately NOT touched (a flagged graph stays flagged).
    case "adjudication_saved":
      return { session: { ...session, adjudication: event.adjudication } };
    case "error":
      return session.status === "running" || session.status === "loading"
        ? { session: { ...session, status: "error", error: event.message } }
        : { session };
    default:
      return { session };
  }
}
