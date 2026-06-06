# Contributing to Plexus

Thanks for your interest. Plexus is early — the foundations are still being laid, so the highest-value contributions are discussion and small, focused PRs.

## Running it

- **Sidecar (.NET):** see the README. `dotnet run --project sidecar/Plexus.Sidecar`.
- **Frontend (Tauri + Vite/React):** coming once P0 lands.

## The Block contract is the heart of the project

[`contract/blocks.ts`](contract/blocks.ts) is the source of truth. The .NET sidecar mirrors it in `sidecar/Plexus.Sidecar/Contract/`. **If you change one, change both**, and bump `BLOCK_SCHEMA_VERSION` for any breaking change.

## Proposing a new block type

Each new block type costs prompt-instruction budget and a renderer, so we grow the catalog deliberately. To propose one, open an issue describing:

1. **What** the block represents and **why** markdown/table/code can't express it well.
2. The **TypeScript shape** (fields, optionality).
3. A short example of the **model output** that would produce it.

If accepted, a block type touches four places: `contract/blocks.ts`, the .NET mirror + JSON schema, the system-prompt instructions, and a frontend renderer.

## Style

- Match the surrounding code's conventions.
- Keep the v1 catalog small. When in doubt, a `markdown` block is the right default.
