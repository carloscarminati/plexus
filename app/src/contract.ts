// Frontend mirror of contract/blocks.ts (types only — erased at build).
// Keep in sync with the root contract and the .NET Contract/*.cs.

export const BLOCK_SCHEMA_VERSION = 1;

export type Block =
  | MarkdownBlock
  | TableBlock
  | LinkCardBlock
  | CodeBlock
  | ChartBlock
  | ChoicesBlock
  | McpUiBlock;

export interface MarkdownBlock {
  type: "markdown";
  text: string;
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
  image?: string;
}

export interface CodeBlock {
  type: "code";
  language: string;
  code: string;
  filename?: string;
}

// C1 — curated Vega-Lite subset (channel-based). NOT a full passthrough: no data
// URLs, transforms, selections, or expressions.
export type ChartMark = "bar" | "line" | "point" | "arc" | "area" | "rect";
export type ChartFieldType = "quantitative" | "nominal" | "ordinal" | "temporal";
export interface ChartChannel {
  field: string;
  type?: ChartFieldType;
}
export interface ChartEncoding {
  x?: ChartChannel;
  y?: ChartChannel;
  color?: ChartChannel;
  theta?: ChartChannel; // magnitude — for arc
  size?: ChartChannel; // for point
}
export interface ChartBlock {
  type: "chart";
  mark: ChartMark;
  data: Record<string, string | number | boolean | null>[]; // inline records
  encoding: ChartEncoding;
  title?: string;
  legend?: boolean;
  stack?: boolean; // bar/area
}

export interface ChoicesBlock {
  type: "choices";
  prompt?: string;
  options: { id: string; label: string }[];
}

export interface McpUiBlock {
  type: "mcp_ui";
  resourceUri: string;
  mimeType: string;
}

export interface Node {
  id: string;
  parentId: string | null;
  mergeParents?: string[]; // P2 DAG merge: extra parents beyond parentId
  role: "user" | "assistant";
  kind?: "deliverable"; // X1: a synthesized decision brief — null/absent otherwise
  createdAt: string;
  blocks: Block[];
  raw: string;
  meta?: {
    model?: string;
    providerId?: string;
    tokensIn?: number;
    tokensOut?: number;
    costUsd?: number;
    latencyMs?: number;
    reason?: string;
    policy?: string; // canonical effective policy ("auto:cost", "manual:<id>")
    toolCalls?: ToolCallRecord[]; // M0: MCP tool invocations during this turn
  };
}

export interface ToolCallRecord {
  serverId: string;
  tool: string;
  args: unknown;
  resultSummary: string;
  readOnly: boolean;
  approved: boolean;
}

export interface Graph {
  id: string;
  title?: string;
  nodes: Node[];
  edges: { from: string; to: string }[];
  defaultPolicy?: RoutingPolicy;
}

// ── Model routing (R1) ──────────────────────────────────────────────────────

export type RoutingObjective = "cost" | "quality" | "balanced";

export type RoutingPolicy =
  | { kind: "manual"; modelId: string }
  | { kind: "auto"; objective: RoutingObjective; budgetPerTurn?: number };

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

// ── Settings (consolidated config the panel surfaces) ───────────────────────

export interface McpTransportView {
  kind: "stdio" | "http";
  command?: string;
  args?: string[];
  env?: Record<string, string>;
  url?: string;
}

export interface McpServerView {
  id: string;
  name: string;
  transport: McpTransportView;
  enabled: boolean;
  toolPolicy?: string;
  httpCredentialSet?: boolean; // true if an HTTP credential is stored in the keychain
}

export interface AppSettingsView {
  confirmTimeoutSeconds: number;
  defaultPolicy: RoutingPolicy;
  anthropicKeyConfigured: boolean;
  mcpServers: McpServerView[];
}

export type ClientEvent =
  | { type: "load_graph"; graphId: string }
  | { type: "list_graphs" }
  | { type: "new_graph"; title?: string }
  | { type: "send_message"; graphId: string; fromNodeId: string | null; fromNodeIds?: string[]; text: string; policy?: RoutingPolicy }
  | { type: "intent"; graphId: string; nodeId: string; kind: string; payload: unknown; policy?: RoutingPolicy }
  | { type: "set_session_policy"; graphId: string; policy: RoutingPolicy }
  | { type: "list_models" }
  | { type: "tool_confirmation"; toolUseId: string; approved: boolean }
  // R1 §4.2 — re-run the input that produced an assistant node with a (usually
  // stronger) model as a SIBLING branch (same parent), for side-by-side compare.
  | { type: "escalate"; graphId: string; nodeId: string; policy?: RoutingPolicy }
  // X1 — synthesize the selected branches into a decision-brief deliverable node.
  | { type: "synthesize"; graphId: string; fromNodeIds: string[]; policy?: RoutingPolicy }
  // Graph management — rename / delete a graph.
  | { type: "set_graph_title"; graphId: string; title?: string }
  | { type: "set_graph_pinned"; graphId: string; pinned: boolean }
  | { type: "delete_graph"; graphId: string }
  // Settings — read + edit consolidated config (secrets go to the keychain).
  | { type: "get_settings" }
  | { type: "set_general_settings"; confirmTimeoutSeconds: number }
  | { type: "set_default_policy"; policy: RoutingPolicy }
  | { type: "set_anthropic_key"; key: string }
  | { type: "delete_anthropic_key" }
  | { type: "set_mcp_server"; server: McpServerView; httpCredential?: string }
  | { type: "delete_mcp_server"; id: string };

export type ServerEvent =
  | { type: "graphs"; graphs: { id: string; title?: string; updatedAt?: string; pinned?: boolean }[] }
  | { type: "graph"; graph: Graph }
  | { type: "node_created"; node: Node }
  | { type: "turn_started"; nodeId: string; parentId: string | null }
  | { type: "turn_delta"; nodeId: string; blocks: Block[] }
  | { type: "turn_completed"; node: Node }
  | { type: "models"; models: ModelInfo[] }
  | {
      type: "tool_confirmation_request";
      nodeId: string;
      toolUseId: string;
      serverId: string;
      serverName: string;
      tool: string;
      args: unknown;
      readOnly: boolean;
    }
  | ({ type: "settings" } & AppSettingsView)
  | { type: "error"; message: string };
