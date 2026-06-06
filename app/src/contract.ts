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

export interface ChartBlock {
  type: "chart";
  chart: "line" | "bar" | "scatter";
  xLabels?: string[];
  series: { name?: string; values: number[] }[];
  xTitle?: string;
  yTitle?: string;
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
  role: "user" | "assistant";
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
  };
}

export interface Graph {
  id: string;
  title?: string;
  nodes: Node[];
  edges: { from: string; to: string }[];
}

export type ClientEvent =
  | { type: "load_graph"; graphId: string }
  | { type: "list_graphs" }
  | { type: "new_graph"; title?: string }
  | { type: "send_message"; graphId: string; fromNodeId: string | null; text: string }
  | { type: "intent"; graphId: string; nodeId: string; kind: string; payload: unknown };

export type ServerEvent =
  | { type: "graphs"; graphs: { id: string; title?: string }[] }
  | { type: "graph"; graph: Graph }
  | { type: "node_created"; node: Node }
  | { type: "turn_started"; nodeId: string; parentId: string | null }
  | { type: "turn_delta"; nodeId: string; blocks: Block[] }
  | { type: "turn_completed"; node: Node }
  | { type: "error"; message: string };
