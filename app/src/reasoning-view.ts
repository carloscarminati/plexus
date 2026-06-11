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
export interface ArgumentView {
  frame?: { id: string; text: string };
  facts: FactItem[];
  uncertainties: UncertaintyItem[];
  hypotheses: HypothesisItem[];
  evaluation: EvaluationRow[];
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

export function buildArgumentView(
  graph: Graph,
  diagnostics: ReasoningDiagnostic[],
  openUncertainties: string[],
): ArgumentView {
  const nodeById = new Map(graph.nodes.map((n) => [n.id, n]));
  const diagFor = (id: string) => diagnostics.filter((d) => d.nodeId === id);

  // Each role group in stable-id order: labels AND row order are then a deterministic
  // function of the persisted ids, so a reordered (or reloaded) graph renders identically.
  const byRole = (role: string) =>
    graph.nodes.filter((n) => n.reasoning?.role === role).sort((x, y) => stableIdCompare(x.id, y.id));

  // Assign display labels (F1, H1, U1) by id-rank within the role — never by array index.
  const label = new Map<string, string>();
  const labelGroup = (role: string, prefix: string) =>
    byRole(role).forEach((n, i) => label.set(n.id, `${prefix}${i + 1}`));
  labelGroup("fact", "F");
  labelGroup("hypothesis", "H");
  labelGroup("uncertainty", "U");
  const lbl = (id: string) => label.get(id) ?? id;

  // F4: prose carries persisted node ids (canonicalized at persist); rewrite each whole-word
  // id token to its display label, reusing the SAME id→label map the edges use — so prose
  // and edges show one label per node. An id with no label (or any other token) is left as-is.
  const proseRe = label.size
    ? new RegExp("\\b(?:" + [...label.keys()].map((k) => k.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")).join("|") + ")\\b", "g")
    : null;
  const relabel = (text: string) => (proseRe ? text.replace(proseRe, (t) => label.get(t) ?? t) : text);

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
    conclusion,
    diagnostics,
  } satisfies ArgumentView;
}

// ── dev round-trip flow (pure; the hook + a mock WS drive the same reducer) ──────────
export interface ReasoningSession {
  status: "idle" | "running" | "loading" | "ready" | "error";
  graph: Graph | null;
  diagnostics: ReasoningDiagnostic[];
  openUncertainties: string[];
  adjudication: Adjudication | null; // ADR-0002 Rx.2.0 — the human decision, beside (never folded into) the reasoning
  error?: string;
}

export const emptyReasoningSession: ReasoningSession = {
  status: "idle",
  graph: null,
  diagnostics: [],
  openUncertainties: [],
  adjudication: null,
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
