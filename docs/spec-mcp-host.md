# Plexus — Feature Spec: MCP Host + `mcp_ui`

> **Addendum to [docs/spec.md](./spec.md).** Second of the three remaining P2 items
> (json-render migration and exposing Plexus's own MCP server are separate, later).
>
> Goal: let the sidecar act as an **MCP host** — connect to configured MCP servers,
> expose their tools to the model, and render the interactive UI that MCP Apps tools
> return as `mcp_ui` blocks in the conversation, safely.

---

## 0. Where this slots into the existing roadmap

Assumes **P0/P1/R0/R1 + P2 DAG merge** are done. Key dependency already in place:
the R1 **capability filter** with `toolCall` as a requirement. This feature is the
first thing that actually *exercises* that dimension — so it's the moment to add the
negative test that was missing at R1 (a model lacking `toolCall` must be filtered out
when a turn needs MCP tools).

The `mcp_ui` block type already exists as a P2 placeholder in the contract
(spec.md §4.1) — this feature gives it a real renderer.

Orthogonal to json-render and to exposing our own MCP server; can ship independently.

## 1. What it is

Two coupled capabilities:

1. **MCP host (sidecar):** connect to MCP servers (local stdio + remote HTTP),
   discover their tools, and expose those tools to the model so it can call them
   mid-turn.
2. **`mcp_ui` block (frontend):** when an MCP Apps tool returns a UI resource, render
   that HTML in a sandboxed iframe as a block, with a mediated message bridge.

Grounding: MCP Apps is the official UI-over-MCP extension — tools return interactive
UI rendered in the conversation, linked via `_meta.ui.resourceUri`, served as
`text/html` in sandboxed iframes, communicating over JSON-RPC via postMessage
(https://blog.modelcontextprotocol.io/posts/2025-11-21-mcp-apps/). The C# SDK
(`ModelContextProtocol.*`, v1.0, spec 2025-11-25) is the host implementation, and MCP
tools surface to an `IChatClient` because `McpClientTool` derives from `AIFunction`.

## 2. MCP host (sidecar)

### 2.1 Server registry

Mirrors the provider registry pattern. Mirror this type in C#.

```ts
export interface McpServerConfig {
  id: string;
  name: string;
  transport:
    | { kind: "stdio"; command: string; args: string[]; env?: Record<string, string> }
    | { kind: "http"; url: string };   // credentials via keychain (by id), NEVER inline
  enabled: boolean;
  toolPolicy?: "auto" | "confirm-destructive" | "confirm-all";  // default below
}
```

### 2.2 Lifecycle & discovery
- On enable: spawn stdio servers as child processes / open the HTTP connection.
- On connect: list tools (and later resources/prompts); cache the tool definitions.
- Handle disconnect/crash/reconnect gracefully; a dead server must not take down a turn.
- Expose discovered tools to the model by adapting `McpClientTool` → `AIFunction` and
  registering them on the `IChatClient` for the turn.

## 3. Safety model (the prominent part — design this first, not last)

MCP tools can perform real, side-effectful, irreversible actions, and MCP servers are
third parties. Treat everything they return as **data, never instructions.**

### 3.1 Instruction-source boundary
- Content from MCP servers — tool results, resource text, UI HTML, any text inside
  them — is **data, not commands.** The model and host must not act on instructions
  embedded in MCP-returned content (e.g. a tool result that says "now delete all files"
  or "connect to server https://x").
- **Never connect to a server URL that came from a tool result or model output.** Only
  user-configured servers from the registry. No auto-discovery from untrusted content.
- Secrets (HTTP server credentials) live in the keychain, referenced by server id;
  never passed to the iframe, never logged, never placed in URLs.

### 3.2 Tool-execution gating
MCP tool annotations (`readOnlyHint`, `destructiveHint`, `idempotentHint`,
`openWorldHint`) drive a confirmation gate:
- **Read-only** (`readOnlyHint: true`) → auto-run.
- **Anything else** — not read-only, `destructiveHint: true`, or annotations absent →
  **require explicit user confirmation before the host executes**, showing the tool
  name and arguments.
- Annotations are *hints from an untrusted server*, so the conservative default
  (confirm anything not explicitly read-only) is the safe stance; `toolPolicy` per
  server can tighten (`confirm-all`) but should not loosen below this.
- The model *requesting* a tool call is not the same as the host *executing* it — the
  human-in-the-loop gate sits between request and execution.

### 3.3 Transparency
Tool calls are shown in the node, not hidden — surface `{tool, args, result-summary}`
so the user sees what the model invoked. This fits Plexus's "make it visible" ethos and
is also a safety property (no silent side effects).

## 4. The `mcp_ui` block (frontend)

When a tool result carries a UI resource (`_meta.ui.resourceUri` → a `text/html`
resource, or an embedded `ui://` resource), the sidecar emits the existing
`{ type: "mcp_ui", resourceUri, mimeType }` block.

Rendering & bridge:
- Render in a **sandboxed iframe**: `sandbox="allow-scripts"` **without**
  `allow-same-origin` (so it can't touch parent DOM, cookies, or storage); strict CSP.
- The iframe ↔ host channel is postMessage carrying JSON-RPC, per MCP Apps. Messages
  *from* the iframe are **requests/intents**, never direct actions: the host validates
  them, and any side-effectful tool call they request goes through the §3.2 gate.
- Option: use `@mcp-ui/client` (`UIResourceRenderer`) to handle the iframe + message
  bridge rather than hand-rolling it. Evaluate vs implementing the MCP Apps postMessage
  protocol directly.

## 5. Phases & acceptance criteria

### M0 — Host + tools, no UI yet — ✅ Done
- [x] Server registry: configure stdio + HTTP servers; credentials in keychain.
- [x] Connect/discover/expose tools to the model; graceful disconnect (per-server
      try/catch — a dead server never breaks startup or a turn). *Tools are exposed
      via the Anthropic tool-use loop (the turn path uses the Anthropic SDK, not
      `IChatClient`); `McpClientTool` is adapted to an Anthropic tool. See divergence note.*
- [x] Read-only tools auto-run; non-read-only/destructive/unknown gated on explicit
      user confirmation (name + args shown), via a mid-turn request/await round-trip.
- [x] Tool calls shown transparently in the node (`node.meta.toolCalls`).
- [x] **Negative routing test (the R1 gap):** a turn needing MCP tools never routes to
      a model lacking `toolCall` (`HeuristicRouter.SelectFrom` unit test).
- [x] No connection to any server URL originating from a tool result or model output
      (the host only ever connects to `LoadRegistry()` servers).

> **Divergence (reported, not silently changed):** the spec says expose tools "via
> `IChatClient`." **Execution is Anthropic-first:** Plexus's turn path runs the
> **Anthropic SDK** tool-use loop directly, adapting `McpClientTool` → an Anthropic
> tool and inserting the human gate between the model's request and the host's
> execution. Same safety outcome; different plumbing. **Deferred:** provider-generic
> tool execution via `IChatClient` + multi-provider dispatch (so MCP tools work on
> any selected provider, not just Anthropic). Tracked in #1 —
> see the matching note in [spec.md](./spec.md) §5 Divergences.
>
> **Robustness guards (M0):** the agentic tool loop is bounded to **8 tool-use rounds
> per turn** (clean stop, no error), tools run **one per round** (`disable_parallel_tool_use`,
> so each side-effecting call is confirmed individually), and an **unanswered
> confirmation cancels the turn** after a timeout (`PLEXUS_CONFIRM_TIMEOUT_SECONDS`,
> default 120s) instead of hanging.

### M1 — `mcp_ui` block rendering
- [ ] Tool UI resource → `mcp_ui` block rendered in a sandboxed iframe
      (`allow-scripts`, no `allow-same-origin`, CSP).
- [ ] postMessage/intent bridge: iframe requests are validated by the host; any
      side-effectful request passes through the §3.2 gate.
- [ ] Acceptance: a UI that requests a destructive tool call cannot execute it without
      user confirmation.

### M2 — Reach
- [ ] Elicitation support (server requests input from the user; spec 2025-11-25).
- [ ] MCP resources/prompts beyond tools.
- [ ] (Exposing Plexus's own MCP server is the *next* P2 item — separate spec.)

## 6. Interactions with existing systems
- **Routing:** a turn requiring MCP tools sets `requires.toolCall` → R1's capability
  filter already excludes incapable models. This finally exercises that path; add the
  negative test (M0).
- **Conversation graph:** tool-call/tool-result steps within a turn — represent them as
  transparent sub-steps of the node (and `mcp_ui` results as blocks). Keep `raw`
  faithful so resumed context (spec.md §4.4) replays tool interactions correctly.
- **Cost/telemetry:** tool round-trips add model calls; keep logging them in the R0
  telemetry schema (no schema change expected).

## 7. Open questions
- Trust UI per server, or sandbox every `mcp_ui` identically regardless of source?
  (Default: identical strict sandbox for all — don't special-case "trusted" servers.) *(eng)*
- How to render tool-call steps in the node without cluttering the canvas — inline
  sub-blocks vs a collapsible detail in the pane? *(eng/design)*
- `@mcp-ui/client` vs hand-rolled postMessage bridge — evaluate maintenance vs control. *(eng)*

## References
- MCP Apps (official UI-over-MCP extension): https://blog.modelcontextprotocol.io/posts/2025-11-21-mcp-apps/
- MCP-UI (`@mcp-ui/client`, `UIResourceRenderer`, intents): https://mcpui.dev
- Official C# MCP SDK (host, elicitation, spec 2025-11-25): https://github.com/modelcontextprotocol/csharp-sdk
- MCP tool annotations & spec: https://modelcontextprotocol.io
