# ADR-0001: Build a .NET-native declarative GenUI contract (vs adopt json-render)

**Status:** Accepted
**Date:** 2026-06-07
**Deciders:** Carlos Carminati (owner, sole dev)

## Context

Plexus renders assistant turns as an ordered array of typed **blocks** (markdown, table, link_card, code, chart, choices, …). The block contract is hand-rolled: the .NET sidecar defines, parses, validates, and persists blocks; the React frontend renders them. Adding a block or chart type is a code change in both halves.

This surfaced concretely: a pie-chart request could not be satisfied, because the `chart` block enum is `line | bar | scatter` and a pie has a different data shape. Every new visual type is two-sided code — a treadmill that grows as the catalog grows by demand.

That prompted a build-vs-adopt evaluation of **json-render** (Vercel Labs, Apache-2.0): a generative-UI framework where a developer-defined catalog (Zod) constrains an LLM to emit a JSON spec that a renderer maps to components, streaming progressively. It is effectively the framework form of Plexus's own block contract, and its MCP-Apps bridge plus multi-target renderers (React/PDF/Remotion/email) overlap directly with Plexus's mcp_ui (M1) and compose/export roadmap.

Forces at play:

- The block-type treadmill is real and will only grow.
- Plexus deliberately puts the contract in .NET: the sidecar owns state and validation (modeled on OpenCode); the React frontend is a renderer of the spec.
- The declarative-GenUI space (json-render, Google **A2UI**, AG-UI, MCP Apps) is entirely JS/TS. There is **no .NET-native declarative GenUI layer**, and .NET is both the owner's core expertise and the ecosystem of the client base (mining / Blazor / Azure).
- json-render is ~5 months old with ~200 releases — high churn. Betting the product's core layer on it is risky, and adopting it as core would invert the contract into JS, against the deliberate design.

## Decision

**Build a .NET-native declarative GenUI contract**, synthesizing the best concepts of json-render and A2UI, rather than adopting json-render as the core render/spec layer.

The layer is a **means, not an end**: it is Plexus's block contract evolved into a principled declarative layer, built incrementally as the product needs it. It may be **extracted later** as a standalone .NET GenUI library *if it earns it by serving Plexus first*. This ADR explicitly does **not** authorize building a framework in the abstract.

### Design — the synthesis

The core insight, learned from A2UI's own practice: **the format the model emits should be ergonomic; the format that is persisted and ported can be protocol-shaped.** A2UI found its raw protocol too verbose for LLMs and inserts a *mapper* between model output and the protocol. Plexus's .NET layer owns both sides plus the mapper.

- **Model-facing emission (from json-render):** a compact, catalog-constrained spec. The catalog is defined in code; the model prompt and JSON Schema are generated from it; emitted specs are validated and auto-fixed.
- **Persisted / portable representation (from A2UI):** a protocol-shaped payload that **separates UI structure from the data model**, uses an **abstract component tree** (catalog type names, not concrete widgets), and uses **typed value fields** (`valueString`/`valueNumber`/…) for LLM-friendliness.
- **The .NET contribution is the catalog + the mapper:** abstract spec ⇄ ergonomic emission, owned and validated in .NET.

| Concept | Source |
|---|---|
| Catalog defined in code; auto-generated model prompt + JSON Schema; registry mapping type → renderer; validation + auto-fix; ergonomic emission | json-render |
| Structure/data separation; abstract, framework-agnostic component tree (portability); per-surface data model; typed value fields; JSONL streaming | A2UI |
| Bidirectional CRDT data sync | A2UI — **deferred** (interactivity only) |

### Mapping to the Plexus stack

- **Catalog** = C# types + metadata.
- **Schema** = `System.Text.Json.Schema.JsonSchemaExporter` (.NET 9).
- **Model calls** = `IChatClient` (Microsoft.Extensions.AI) — already the provider abstraction.
- **Validation + auto-fix** = against the generated schema, in the sidecar.
- **Streaming** = JSONL over the existing sidecar↔frontend channel (take A2UI's *message model*, not its A2A transport).
- **Renderer registry** = React maps abstract type → component; existing renderers (table, link_card, chart) become entries.
- **Surface ≈ node content.** The graph/branching model stays *outside* the render contract, unchanged.

## Options Considered

### Option A: Build a .NET-native declarative contract (CHOSEN)

| Dimension | Assessment |
|---|---|
| Complexity | Medium — design + incremental build, no new core dependency |
| Cost | Ongoing maintenance (solo dev), bounded by product-driven scope |
| Architecture fit | High — contract stays .NET-owned, sidecar owns state |
| Team familiarity | High — .NET is core expertise |
| Differentiation | High — no .NET-native equivalent exists |

**Pros:** kills the treadmill in .NET; contract stays .NET-owned; protocol-shaped → mappable to A2UI/AG-UI/MCP Apps later without coupling to a pre-1.0 spec; extractable as a genuinely novel .NET library; serves the .NET/Blazor client base.
**Cons:** real, ongoing work; scope-creep risk toward "framework"; forgoes json-render's free multi-target export; model adherence to the spec interacts with routing (small models may fail validation more often).

### Option B: Adopt json-render as the core layer

| Dimension | Assessment |
|---|---|
| Complexity | High — invert the contract into JS, adopt their wire protocol |
| Cost | Low code, high dependency/churn risk |
| Architecture fit | Low — reverses the .NET-owns-the-contract design |
| Team familiarity | Medium — pulls center of gravity into JS |
| Differentiation | Low — "a nice integration", not a contribution |

**Pros:** catalog + mcp_ui (MCP Apps) + multi-target export as one system; philosophically a bullseye fit; less code.
**Cons:** inverts the deliberate architecture; bets the core on a 5-month-old, ~200-release dependency; cedes the product-defining layer; moves center of gravity to JS.

### Option C: Adopt json-render at the export boundary only

| Dimension | Assessment |
|---|---|
| Complexity | Medium |
| Cost | A dependency scoped to export |
| Architecture fit | Medium — live core stays own; export uses their renderers |
| Differentiation | Low for the core |

**Pros:** free PDF/Remotion/email targets for the compose/export arc; low blast radius; a learning vehicle.
**Cons:** still requires block→spec translation; only pays off with multiple export targets; not a contribution.

## Trade-off Analysis

B's appeal is leverage — three roadmap arcs (catalog, mcp_ui, export) collapse into one framework — but its cost is strategic: it inverts the architecture, bets the core on a churning dependency, and yields "a nice integration," not something novel. A keeps the contract where it was deliberately placed (.NET), turns the treadmill problem into an asset (a novel .NET-native layer), and stays interoperable by being *protocol-shaped* rather than protocol-coupled. The decisive question — should the product-defining render contract live in .NET or JS — is answered **.NET**, consistent with the existing design and the owner's expertise and market.

C is not rejected forever: json-render's multi-target renderers remain attractive **at the export boundary** for the compose/export arc, and can be revisited as a separate, low-risk decision then. That does not require adopting it as core.

## Consequences

**Easier**

- New block/chart types become catalog entries, not two-sided code changes.
- The contract stays .NET-owned; the sidecar keeps owning state/validation.
- The same abstract spec can target a future Blazor/MAUI renderer (extractability).
- Vega-Lite chart becomes the first catalog component — proving the model, not a patch.

**Harder**

- Ongoing design/maintenance of the layer falls on a solo dev across multiple projects.
- Model adherence to the spec must be enforced (schema validation + auto-fix + fallback) and interacts with the router (small-tier picks may fail validation more → ties into escalate).

**Revisit**

- Protocol mapping (A2UI / AG-UI / **MCP Apps** — the one with real host adoption today) once those stabilize.
- Bidirectional CRDT data sync when interactive blocks (mcp_ui / M1) need it.
- json-render at the export boundary for compose/export (multi-target).
- Standalone framework extraction — only if the layer earns it by serving Plexus first.

## Action Items (incremental, product-driven)

1. [ ] **C0 — Catalog extraction.** Refactor existing block types into an explicit .NET catalog (types + metadata) with no behavior change. Generate the model prompt + JSON Schema from the catalog. Validate emitted specs against it in the sidecar.
2. [ ] **C1 — First catalog component: Vega-Lite chart.** Add the chart component as a catalog entry (curated Vega-Lite subset, themed via design tokens; reuses `xLabels` + `series[0]` semantics for pie/arc). Proves the catalog + React registry path and resolves the pie/treadmill problem.
3. [ ] **C2 — Protocol-shaped payload.** Introduce structure/data separation, typed value fields, and the ergonomic-emission ↔ protocol-payload mapper; move streaming to JSONL.
4. [ ] **C3 — (later) Interop & interactivity.** Map to MCP Apps / A2UI once stabilized; add CRDT/data-binding when mcp_ui requires it; consider extraction.

## Related

- `docs/spec.md` — block contract (§4)
- `docs/spec-mcp-host.md` — mcp_ui / M1
- `VISION.md` — product north-star
- Issue #1 — Anthropic-only execution path
