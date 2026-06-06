// contract/blocks.ts — the source of truth for the Block contract.
//
// An assistant turn's content is an ordered array of Blocks. A Block is a
// discriminated union on `type`. The .NET sidecar mirrors these types
// (see sidecar/Contract/*.cs) and the frontend renders them.
//
// Versioned: bump BLOCK_SCHEMA_VERSION on any breaking change. Older clients
// that don't understand a future block type fall back to rendering `raw`.

export const BLOCK_SCHEMA_VERSION = 1;

export type Block =
  | MarkdownBlock
  | TableBlock
  | LinkCardBlock
  | CodeBlock
  | ChartBlock // P1
  | ChoicesBlock // P1
  | McpUiBlock; // P2

export interface MarkdownBlock {
  type: "markdown";
  text: string; // GFM. The fallback / default block.
}

export interface TableBlock {
  type: "table";
  columns: { key: string; label: string; align?: "left" | "right" | "center" }[];
  rows: Record<string, string | number | boolean | null>[];
  caption?: string;
}

export interface LinkCardBlock {
  type: "link_card";
  url: string;
  title?: string;
  description?: string;
  image?: string; // OG image / site preview; resolved by the sidecar.
}

export interface CodeBlock {
  type: "code";
  language: string;
  code: string;
  filename?: string;
}

export interface ChartBlock {
  // P1
  type: "chart";
  chart: "line" | "bar" | "scatter";
  xLabels?: string[];
  series: { name?: string; values: number[] }[];
  xTitle?: string;
  yTitle?: string;
}

export interface ChoicesBlock {
  // P1 — interactive
  type: "choices";
  prompt?: string;
  options: { id: string; label: string }[];
  // The frontend does NOT mutate state. On click it bubbles an *intent*
  // back to the sidecar, which decides what to do.
}

export interface McpUiBlock {
  // P2
  type: "mcp_ui";
  resourceUri: string; // ui://... from an MCP Apps tool result
  mimeType: string; // e.g. text/html — rendered in a sandboxed iframe
}

// ── Conversation graph ──────────────────────────────────────────────────────

export interface Node {
  id: string;
  parentId: string | null; // primary parent. Tree by default.
  mergeParents?: string[]; // P2 DAG merge: extra parents beyond parentId (union-of-ancestors)
  role: "user" | "assistant";
  createdAt: string; // ISO; used to order reconstructed history
  blocks: Block[]; // for user turns this is usually one markdown block
  raw: string; // the model's original text — re-fed verbatim on resume
  meta?: {
    model?: string;
    providerId?: string;
    tokensIn?: number;
    tokensOut?: number;
    costUsd?: number; // estimated $ for this turn (from the registry)
    latencyMs?: number;
    reason?: string; // why this model was picked (router)
    policy?: string; // canonical effective policy ("auto:cost", "manual:<id>")
  };
}

export interface Graph {
  id: string;
  title?: string;
  nodes: Node[];
  edges: { from: string; to: string }[]; // parent -> child, derivable from parentId
  defaultPolicy?: RoutingPolicy; // session default routing policy (R1)
}

// ── Model routing (R1) — see docs/spec-model-routing.md ─────────────────────

export type RoutingObjective = "cost" | "quality" | "balanced";

export type RoutingPolicy =
  | { kind: "manual"; modelId: string }
  | { kind: "auto"; objective: RoutingObjective; budgetPerTurn?: number };

// One curated candidate model (NOT the full models.dev catalog) for the picker.
export interface ModelInfo {
  id: string;
  providerId: string;
  tier: "small" | "mid" | "large";
  costInPerMTok: number;
  costOutPerMTok: number;
  contextWindow: number;
  toolCall: boolean;
  vision: boolean;
}

// ── Transport (local WebSocket: sidecar <-> frontend) ───────────────────────
// The frontend renders, never thinks. It sends intents; the sidecar owns state.

export type ClientEvent =
  | { type: "load_graph"; graphId: string }
  | { type: "list_graphs" }
  | { type: "new_graph"; title?: string }
  // fromNodeId null = start of a fresh graph; otherwise resume/branch from that node.
  // fromNodeIds (P2 DAG merge): ≥2 nodes → context = union-of-ancestors of all of them.
  // policy = the resolved routing policy (per-node override ?? session default).
  | { type: "send_message"; graphId: string; fromNodeId: string | null; fromNodeIds?: string[]; text: string; policy?: RoutingPolicy }
  // P1 — a `choices`/`mcp_ui` block fired an interactive intent.
  | { type: "intent"; graphId: string; nodeId: string; kind: string; payload: unknown; policy?: RoutingPolicy }
  // R1 — persist the session default routing policy.
  | { type: "set_session_policy"; graphId: string; policy: RoutingPolicy }
  // R1 — request the curated candidate set.
  | { type: "list_models" };

export type ServerEvent =
  | { type: "graphs"; graphs: { id: string; title?: string }[] }
  | { type: "graph"; graph: Graph }
  | { type: "node_created"; node: Node }
  | { type: "turn_started"; nodeId: string; parentId: string | null }
  // Progressive rendering (P1): partial blocks as the model emits them.
  | { type: "turn_delta"; nodeId: string; blocks: Block[] }
  | { type: "turn_completed"; node: Node }
  // R1 — the curated candidate models for the Manual picker.
  | { type: "models"; models: ModelInfo[] }
  | { type: "error"; message: string };
