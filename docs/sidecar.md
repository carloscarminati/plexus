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

Client → server: `list_graphs`, `new_graph`, `load_graph`, `send_message`, `intent` (P1).
Server → client: `graphs`, `graph`, `node_created`, `turn_started`, `turn_delta` (P1), `turn_completed`, `error`.

A turn: `send_message {graphId, fromNodeId, text}` →
`node_created` (the user node) → `turn_started` (reserved assistant id) → `turn_completed` (assistant node with blocks).

## Block production

- **Strategy (a) — primary:** the system prompt ([`Model/SystemPrompt.cs`](../sidecar/Plexus.Sidecar/Model/SystemPrompt.cs)) instructs the model to return `{ "blocks": [...] }`; we parse + validate it.
- **Strategy (b) — fallback:** [`Model/FallbackParser.cs`](../sidecar/Plexus.Sidecar/Model/FallbackParser.cs) lifts markdown tables, fenced code, and standalone URLs out of plain prose. Always renderable.

> We use prompt-guided JSON rather than the API's *strict* structured outputs because the `table.rows` map (arbitrary keys) can't be expressed under strict-schema rules (open `additionalProperties` is forbidden). A hardening pass can move strategy (a) to a constrained tool / structured output — likely by representing table rows as cell arrays under strict mode.

## Spec → implementation status (P0)

| P0 acceptance criterion | Status |
| --- | --- |
| Tauri boots; frontend ↔ sidecar over WebSocket | sidecar side done; frontend pending (needs Node/Rust) |
| API key in OS keychain; never in renderer | done (`Services/KeychainService.cs`, macOS keychain + env fallback) |
| Single linear conversation end-to-end | done server-side (verify with a key) |
| Assistant turns render as blocks `{markdown, table, link_card, code}` | done (contract + parsing; renderers pending in frontend) |
| Strategy (a) for one provider + (b) fallback | done (Anthropic / Claude) |
| `link_card` resolves an OG image server-side | done (`Services/LinkCardResolver.cs`) |
| Graph persists to SQLite and reloads | done (`Persistence/GraphStore.cs`) |

The ancestor-walk resume mechanic (spec §4.4) is already implemented (`Model/ConversationService.BuildHistory`), so branching/resume (P1) has its foundation.

## Caveat for later

The app currently relies on reflection-based `System.Text.Json`. If we ever trim/AOT the sidecar, the polymorphic block serialization needs source-generated `JsonSerializerContext`.
