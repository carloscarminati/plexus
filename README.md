# Plexus

> A desktop app where an AI conversation is a **graph of richly-rendered blocks** on a canvas. Branch from any node, resume from any node, and each assistant turn is rendered in its *best representation* (table, link card, chart, code, interactive widget) instead of a wall of markdown.

Plexus fixes two things open chat loses:

1. **Shape.** A list renders as a table, a URL as a preview card, tabular data as a grid — not prose.
2. **Branching.** Fork at any earlier point, explore two directions, keep both visible with their context intact.

It's **local-first** (the graph lives on disk) and **provider-agnostic** (bring your own API key).

## Architecture

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

- **Frontend renders, never thinks.** It receives blocks + node/edge events over a local WebSocket.
- **Sidecar owns everything stateful**: the graph, persistence, model calls, prompt caching.
- The single most important artifact is the **Block contract** — [`contract/blocks.ts`](contract/blocks.ts). The .NET side mirrors it.

## Repo layout

| Path        | What                                                          |
| ----------- | ------------------------------------------------------------ |
| `contract/` | `blocks.ts` — shared Block contract (source of truth)        |
| `sidecar/`  | .NET solution — the brain (WebSocket, SQLite, model calls)   |
| `app/`      | Tauri shell + Vite/React frontend (coming)                   |
| `docs/`     | architecture notes, screenshots                              |
| `SPEC.md`   | the guiding document / contract                              |

## Running the sidecar (P0)

Requires the [.NET SDK](https://dotnet.microsoft.com/) (10+).

```bash
# Provide your Anthropic API key (the sidecar prefers the OS keychain, falls back to env).
export ANTHROPIC_API_KEY=sk-ant-...

cd sidecar
dotnet run --project Plexus.Sidecar
```

The sidecar listens on `ws://127.0.0.1:8765/ws` and persists the graph to `~/.plexus/plexus.sqlite`.

To store the key in the macOS keychain instead of an env var:

```bash
security add-generic-password -a plexus -s plexus-anthropic-key -w "sk-ant-..."
```

## Status

Building toward **P0** (walking skeleton): linear conversation, blocks `{markdown, table, link_card, code}`, structured-output + heuristic fallback, OG-image resolution, SQLite persistence. See [`SPEC.md`](SPEC.md) §5 for the phase plan.

## License

[MIT](LICENSE).
