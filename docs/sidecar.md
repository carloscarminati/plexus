# Sidecar (the brain) — notes

The .NET sidecar owns all state: the conversation graph, persistence, model calls, and block orchestration. The frontend renders, never thinks.

## Run

```bash
export ANTHROPIC_API_KEY=sk-ant-...        # or store in the macOS keychain (see README)
dotnet run --project sidecar/Plexus.Sidecar
```

- HTTP/WebSocket: `127.0.0.1:8765` (loopback only).
- `GET /health` → `{ "status": "ok", "schemaVersion": 1 }`.
- WebSocket: `ws://127.0.0.1:8765/ws`.
- Graph DB: `~/.plexus/plexus.sqlite` (WAL).

## WebSocket protocol

Defined in [`contract/blocks.ts`](../contract/blocks.ts) (`ClientEvent` / `ServerEvent`) and mirrored in `Contract/Transport.cs`. Both are discriminated unions on `type`. The wire format is camelCase JSON.

Client → server: `list_graphs`, `new_graph`, `load_graph`, `send_message`, `intent` (P1), `set_session_policy`, `list_models` (R1), `tool_confirmation` (M0), `escalate` (R1 §4.2), `set_graph_title`, `delete_graph` (P2), `get_settings`, `set_general_settings`, `set_default_policy`, `set_anthropic_key`, `delete_anthropic_key`, `set_mcp_server`, `delete_mcp_server` (Settings).
Server → client: `graphs`, `graph`, `node_created`, `turn_started`, `turn_delta` (P1), `turn_completed`, `models` (R1), `tool_confirmation_request` (M0), `settings` (Settings), `error`.

A turn: `send_message {graphId, fromNodeId, text}` →
`node_created` (the user node) → `turn_started` (reserved assistant id) → `turn_completed` (assistant node with blocks).

## Block production

- **Strategy (a) — primary:** the system prompt ([`Model/SystemPrompt.cs`](../sidecar/Plexus.Sidecar/Model/SystemPrompt.cs)) instructs the model to return `{ "blocks": [...] }`; we parse + validate it.
- **Strategy (b) — fallback:** [`Model/FallbackParser.cs`](../sidecar/Plexus.Sidecar/Model/FallbackParser.cs) lifts markdown tables, fenced code, and standalone URLs out of plain prose. Always renderable.

> We use prompt-guided JSON rather than the API's *strict* structured outputs because the `table.rows` map (arbitrary keys) can't be expressed under strict-schema rules (open `additionalProperties` is forbidden). A hardening pass can move strategy (a) to a constrained tool / structured output — likely by representing table rows as cell arrays under strict mode.

## Spec → implementation status (P0)

| P0 acceptance criterion | Status |
| --- | --- |
| Tauri boots; frontend ↔ sidecar over WebSocket | done (Tauri shell + React frontend) |
| API key in OS keychain; never in renderer | done (`Services/KeychainService.cs`, macOS keychain + env fallback) |
| Single linear conversation end-to-end | done server-side (verify with a key) |
| Assistant turns render as blocks `{markdown, table, link_card, code}` | done (contract + parsing + frontend renderers) |
| Strategy (a) for one provider + (b) fallback | done (Anthropic / Claude) |
| `link_card` resolves an OG image server-side | done (`Services/LinkCardResolver.cs`) |
| Graph persists to SQLite and reloads | done (`Persistence/GraphStore.cs`) |

The ancestor-walk resume mechanic (spec §4.4) is already implemented (`Model/ConversationService.BuildHistory`), so branching/resume (P1) has its foundation.

## Spec → implementation status (P1)

| P1 acceptance criterion | Status |
| --- | --- |
| Conversation renders as a tree on a React Flow canvas with edges | done (`app/src/CanvasView.tsx`, dagre layout) |
| Branch: pick any node, send a message, get a new child node | done (select node → compose; tree forks) |
| Resume-from-node reconstructs context (§4.4) | done (`ConversationService.BuildHistory`) |
| Add `chart` and `choices` blocks (incl. intent round-trip) | done (SVG chart renderer; choices click → `intent` → sidecar branches from that node) |
| Prompt-prefix caching enabled | done — `cache_control` on the system prompt + the shared-ancestor prefix breakpoint. Hits only above the model's min cacheable prefix (4096 tok on Opus 4.8), so short conversations log `cacheRead=0`; longer ones reuse the prefix across sibling branches. |

Intent round-trip: a `choices` click sends `{nodeId, kind:"choice", payload:{id,label}}`; the sidecar injects the chosen label as a new user message branching from `nodeId` (`WebSocketHub.HandleIntentAsync`).

## Model routing (R0) — see [spec-model-routing.md](spec-model-routing.md)

`sidecar/Plexus.Sidecar/Routing/`:

- **Registry** (`ModelRegistry`) — provider configs (`~/.plexus/providers.json`, default Anthropic) × model metadata pulled from [models.dev](https://models.dev) (`api.json`), cached at `~/.plexus/models.json` and refreshed daily by `RegistryRefreshService`. Pricing/capabilities are never hand-maintained; a thin fallback table covers current Anthropic models not yet listed.
- **Routing seam** (`IModelRouter`) — `RoutingContext` (messages + `requires` + `policy`) → `ModelChoice`. `ManualRouter` returns the chosen model and runs the capability filter as a guardrail (warns in `reason` if the model can't meet `requires`). `HeuristicRouter`/`LearnedRouter` slot in behind the same interface (R1/R2).
- **Telemetry** (`SqliteTelemetrySink`) — every call logs and persists `{modelId, providerId, tokensIn, tokensOut, costUsd, latencyMs, policy, reason}` to a `telemetry` table. This is the data R1 needs before auto-routing can be validated.
- **Branch-level routing** (§4.1) — a turn inherits the model of the nearest assistant ancestor, so a branch stays sticky to its model and keeps its prompt cache.
- API keys are keychain-resolved per provider (`KeychainService.GetKey(providerId)` → `plexus-{providerId}-key`).

The chosen model + cost + latency + reason are stored in `node.meta` and shown as a badge on each canvas card and in the detail pane. Execution is still Anthropic-only; multi-provider dispatch (each provider an `IChatClient`) is R1.

### R0 status

| R0 acceptance criterion | Status |
| --- | --- |
| Providers configurable; API keys in keychain by provider id | done |
| Model metadata pulled + cached from models.dev; scheduled refresh | done (7400+ models) |
| `IModelRouter` exists; `ManualRouter` implemented | done |
| Per-session default + per-node model; choice stored in `node.meta` | done (branch-sticky default; per-node override is the data path — UI selector is a follow-up) |
| Telemetry per call `{model, provider, tokensIn/out, cost, latencyMs, policy, reason}` | done (log + SQLite) |
| Each node displays a model badge + cost | done |

### R1 status — heuristic auto-routing

`HeuristicRouter` slots behind the unchanged `IModelRouter`; `CompositeRouter` dispatches `manual → ManualRouter`, `auto → HeuristicRouter`.

| R1 acceptance criterion | Status |
| --- | --- |
| `RoutingPolicy` toggle in the UI: Manual / Auto-cost / Auto-quality / Auto-balanced | done (unified `PolicyPicker`, one component for session default + per-node override) |
| `HeuristicRouter`: capability-filter → tiering (signals: prompt+history length, code, tool/structured-output, depth) | done |
| Manual override always wins over auto | done |
| `ModelChoice.reason` surfaced in the node badge | done (hover) |
| Trivial prompt under Auto-cost → cheapest capable; tool-requiring prompt never picks a non-tool model; never picks a non-structured-output model | done (capability filter; verified) |

Key design points:
- **Curated candidate set** — `CandidateSet` hard-codes per-provider `{small, mid, large}` (anthropic: haiku-4-5 / sonnet-4-6 / opus-4-8). We do **not** route over the 7000+ models.dev catalog; models.dev is metadata-only.
- **Two-step** (§2): capability filter (`structuredOutput` always required; tool/vision/minContext as detected) → policy optimize (cost = cheapest capable; quality = top tier; balanced = complexity-target tier, capped by `budgetPerTurn`).
- **Branch-level stickiness** (§4.1): a turn reuses the branch's model under the same policy; re-routes only when the policy changes or `requires` forces it (sticky `reason` in telemetry).
- **Provider-scoped registry lookups** — bare model ids collide across resellers on models.dev, so candidate/cost lookups are scoped to the candidate's provider; the bare-id index was removed.
- **Model-aware request shape** — adaptive thinking + effort are only sent to models that support them (Opus 4.6+/Sonnet 4.6), not Haiku 4.5 — required once the router can pick a small model.
- Telemetry schema is **unchanged**; `policy`/`reason` now carry the real auto values (`auto:cost`, `auto/cost: cheapest capable (...)`, `auto:cost: sticky branch model (...)`).

## Model graph: DAG merge (P2)

A node has a primary `parentId` plus optional `mergeParents[]`. Selecting ≥2
nodes on the canvas (shift/⌘-click) and sending creates a **merge node** whose
context is the **deduplicated union of every selected node's ancestor path** —
`ConversationService.BuildHistory` walks both `parentId` and `mergeParents` (a
graph traversal, deduped by id, ordered by `createdAt`), so the model sees both
branches at once. The merge node carries edges from all its parents (dashed on
the canvas). Persisted via the `nodes.merge_parents_json` column. Routing
stickiness follows the primary parent.

## MCP host + tools (M0) — see [spec-mcp-host.md](spec-mcp-host.md)

`sidecar/Plexus.Sidecar/Mcp/`. The host connects only to user-configured registry
servers (`~/.plexus/mcp-servers.json`) — never a URL from a tool result or model
output. HTTP credentials are keychain-resolved (`plexus-mcp-{id}-key`), sent as a
bearer header, never logged. On startup it connects each enabled server (stdio =
child process, http = `HttpClientTransport`), discovers tools, and caches them; a
server that fails to connect is logged and skipped.

A turn with any MCP tool available sets `requires.toolCall` → R1's capability filter
picks a tool-capable model. `AnthropicTurnService` runs an agentic tool-use loop:
model → `tool_use` → executor → `tool_result` → model. The executor (`ConversationService`)
applies the **safety gate**: read-only tools auto-run; anything else (or a
`confirm-all` server) emits a `tool_confirmation_request` and awaits the user's
`tool_confirmation` before executing — the hub runs the turn off its receive loop so
the reply can be processed mid-turn. Tool results are **data, never instructions**.
Every call is recorded in `node.meta.toolCalls` (shown in the node) and the tool
transcript is folded into `raw` so resumed branches replay the interactions (§4.4).

The `mcp_ui` block (rendering UI resources in a sandboxed iframe) is **M1** — not in M0.

## Caveat for later

The app currently relies on reflection-based `System.Text.Json`. If we ever trim/AOT the sidecar, the polymorphic block serialization needs source-generated `JsonSerializerContext`.
