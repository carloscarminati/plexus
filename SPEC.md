# Plexus — Spec

> **Working codename: "Plexus"** (a network of connected nodes). Rename freely before going public.
>
> A desktop app where an AI conversation is a **graph of richly-rendered blocks** on a canvas. You branch from any node, pick a node to resume from, and each assistant turn is rendered in its *best representation* (table, link card, chart, code, interactive widget) instead of a wall of markdown.

This document is the contract. It is meant to be read top-to-bottom by a human and used as the guiding document for a Claude Code session. The heart of it is the **Block contract** (section 4); everything else is scaffolding around that.

---

## 1. Problem

Open chat with an AI is linear and textual. Two things are lost:

1. **Shape.** A list is shown as prose when it should be a table; a URL as raw text when it could be a card with the site preview; tabular data as a code fence when it should be a grid.
2. **Branching.** You can only go forward. There is no way to fork at an earlier point, explore two directions, and keep both visible with their context intact.

Plexus addresses both: an adaptive render layer per turn, on top of a branching conversation graph.

## 2. Goals / Non-goals

**Goals (v1)**
- Render each assistant turn as a typed list of **blocks**, choosing the best representation per block.
- Represent the session as a **tree of nodes** on a canvas; select any node and resume the conversation with the context up to that node.
- Be **local-first** (graph persists on disk) and **provider-agnostic** (BYO API key).

**Non-goals (v1) — explicit, to prevent scope creep**
- No DAG merge of multiple branches (union-of-ancestors). Tree only. *(P2)*
- No embedding of provider web sessions (chatgpt.com in a webview). We use APIs so we control the model's output format — the whole render thesis depends on that.
- No multi-device sync / accounts. Local only.
- No full [Vercel json-render](https://github.com/vercel/json-render) catalog in v1. We ship a tiny hand-rolled block catalog and grow into json-render later. *(P2)*
- No authoring of MCP servers. We *consume* one local MCP host; exposing our own MCP server is later. *(P2)*

## 3. Architecture (recap)

```
┌─────────────────────────── Tauri shell ───────────────────────────┐
│  Frontend (Vite + React)            Sidecar (.NET, the "brain")    │
│  ─ canvas: React Flow               ─ conversation graph + state   │
│  ─ block renderers                  ─ persistence (SQLite)         │
│  ─ sandboxed iframes for MCP UI     ─ model API calls + prompt cache│
│         ▲                           ─ MCP host (official C# SDK)    │
│         │  local WebSocket (stream) ─ block-spec orchestration     │
│         └─────────────────────────────────────▲                   │
│  OS keychain (API keys)  ───────────────────────┘                  │
└────────────────────────────────────────────────────────────────────┘
```

- **Frontend renders, never thinks.** It receives blocks and node/edge events over a local WebSocket and draws them.
- **Sidecar owns everything stateful**: the graph, persistence, model calls, prompt caching, and (later) MCP host wiring via the official `ModelContextProtocol` NuGet packages.
- **Transport must stream**: blocks render progressively as the model emits them, so the sidecar→frontend channel pushes partial blocks.

## 4. The Block contract (the core)

An **assistant turn's content is an ordered array of Blocks**. A Block is a discriminated union on `type`. This is the single most important artifact in the project; design it carefully and version it.

### 4.1 Canonical TypeScript types

```ts
// contract/blocks.ts — the source of truth. The .NET side mirrors these.
export const BLOCK_SCHEMA_VERSION = 1;

export type Block =
  | MarkdownBlock
  | TableBlock
  | LinkCardBlock
  | CodeBlock
  | ChartBlock      // P1
  | ChoicesBlock    // P1
  | McpUiBlock;     // P2

export interface MarkdownBlock {
  type: "markdown";
  text: string;                 // GFM. The fallback / default block.
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
  image?: string;               // OG image / site preview ("portada"); resolved by sidecar
}

export interface CodeBlock {
  type: "code";
  language: string;
  code: string;
  filename?: string;
}

export interface ChartBlock {            // P1
  type: "chart";
  chart: "line" | "bar" | "scatter";
  xLabels?: string[];
  series: { name?: string; values: number[] }[];
  xTitle?: string;
  yTitle?: string;
}

export interface ChoicesBlock {          // P1 — interactive
  type: "choices";
  prompt?: string;
  options: { id: string; label: string }[];
  // The frontend does NOT mutate state. On click it bubbles an *intent*
  // back to the sidecar (see 4.4), which decides what to do.
}

export interface McpUiBlock {            // P2
  type: "mcp_ui";
  resourceUri: string;          // ui://... from an MCP Apps tool result
  mimeType: string;             // e.g. text/html — rendered in a sandboxed iframe
}
```

### 4.2 How the model produces blocks

Two strategies, both implemented; (a) is primary, (b) is the safety net.

**(a) Model declares the blocks (primary).** Instruct the model (system prompt) to return its turn as a JSON array conforming to the schema. Prefer the provider's structured-output / tool mechanism so the output is constrained, not hoped-for:
- Provide a tool `emit_turn(blocks: Block[])` (or the provider's JSON-schema response format).
- The sidecar validates against the schema before forwarding. Invalid → fall to (b).

**(b) Heuristic fallback parser (universal).** For plain prose (or non-cooperating models), the sidecar parses the text and lifts obvious structures: markdown tables → `table`, bare URLs → `link_card`, fenced code → `code`, everything else → `markdown`. Lossy but always works.

> Keep the catalog *small* in v1 (markdown, table, link_card, code). Each new block type costs prompt-instruction budget and a renderer. Grow deliberately.

### 4.3 Conversation graph model

```ts
export interface Node {
  id: string;
  parentId: string | null;      // single parent (tree). DAG/multi-parent is P2.
  role: "user" | "assistant";
  createdAt: string;            // ISO; used to order reconstructed history
  blocks: Block[];              // for user turns this is usually one markdown block
  raw: string;                  // the model's original text — re-fed verbatim on resume
  meta?: { model?: string; tokensIn?: number; tokensOut?: number };
}

export interface Graph {
  id: string;
  title?: string;
  nodes: Node[];
  edges: { from: string; to: string }[];  // parent -> child, derivable from parentId
}
```

### 4.4 Resuming context from a node (the key mechanic)

When the user selects node `N` and sends a message:
1. Walk from `N` to the root following `parentId`, collecting all ancestors.
2. Sort ancestors by `createdAt`.
3. Serialize to model history: user turns as-is; assistant turns using their stored `raw` text (NOT a re-render — avoid lossy round-trips).
4. Append the new user message, call the model, create a new child node of `N` with the returned blocks.

This is the standard ancestor-walk used by existing canvas chats. The merge case (select multiple nodes → union of ancestors, deduplicated) is deferred to P2.

**Interactive intents.** When a `choices` (or future `mcp_ui`) block fires, the frontend sends an *intent* `{ nodeId, kind, payload }` to the sidecar. The sidecar — not the frontend — decides the next turn (e.g. injects the chosen option as a new user message and continues from that node). This keeps the model in control of state.

**Prompt caching.** Sibling branches share a long common prefix (the path to their shared ancestor). Cache by prefix so exploring many branches stays cheap.

## 5. Phases & acceptance criteria

### P0 — Walking skeleton (the milestone that proves the thesis)
- [ ] Tauri app boots; frontend talks to .NET sidecar over local WebSocket.
- [ ] API key stored in OS keychain; never present in the renderer.
- [ ] Single linear conversation works end-to-end against one provider.
- [ ] Assistant turns render as **blocks** with catalog `{markdown, table, link_card, code}`.
- [ ] Strategy (a) works for one provider; strategy (b) fallback parser implemented.
- [ ] `link_card` resolves an OG image server-side.
- [ ] Graph persists to SQLite and reloads on restart.

### P1 — The canvas
- [ ] Conversation renders as a tree of nodes on a React Flow canvas with edges.
- [ ] User can branch: pick any node, send a message, get a new child node.
- [ ] Resume-from-node reconstructs context correctly (section 4.4).
- [ ] Add `chart` and `choices` blocks (incl. the intent round-trip).
- [ ] Prompt-prefix caching enabled.

### P2 — Reach
- [ ] DAG merge: multi-select nodes, union-of-ancestors context.
- [ ] MCP host (official C# SDK) wired; `mcp_ui` block renders MCP Apps UI resources in a sandboxed iframe.
- [ ] Optional: migrate the block catalog onto Vercel json-render.
- [ ] Optional: expose Plexus's own MCP server.

## 6. Repo setup (first public repo)

Suggested top-level layout:

```
/                README.md, LICENSE, CONTRIBUTING.md, SPEC.md, .gitignore
/contract        blocks.ts (+ generated JSON Schema)  ← shared source of truth
/app             Tauri shell + Vite/React frontend
/sidecar         .NET solution (the brain)
/docs            architecture notes, screenshots
```

Open-source hygiene worth doing from day one:
- **LICENSE**: MIT or Apache-2.0. Apache-2.0 adds an explicit patent grant; MIT is shorter and more common for app projects. Either is fine — just pick one and commit it before the first public push.
- **README**: what it is + a screenshot/GIF as soon as P0 renders anything. A visible demo is what earns stars.
- **CONTRIBUTING.md**: even one short paragraph (how to run the app, how to propose a block type).
- Tag a `v0.1.0` when P0 lands. Shipping a real milestone beats a perfect unreleased one.

## 7. Open questions (resolve during, not before)
- Which provider first for structured output? *(eng)*
- React Flow vs. forking the tldraw branching-chat starter kit for the canvas? React Flow = more control and a better learning path; tldraw kit = faster to a working branch demo. *(eng)*
- Block-schema evolution: how to render an unknown future block type from an older client? (Suggest: clients fall back to rendering `raw`.) *(eng)*

## References
- MCP Apps (official UI-over-MCP extension): https://blog.modelcontextprotocol.io/posts/2025-11-21-mcp-apps/
- MCP-UI (intent-based interactive components): https://mcpui.dev
- Vercel json-render (constrained generative UI from JSON): https://github.com/vercel/json-render
- tldraw branching-chat starter kit: https://tldraw.dev/starter-kits/branching-chat
- Official C# MCP SDK: https://github.com/modelcontextprotocol/csharp-sdk
