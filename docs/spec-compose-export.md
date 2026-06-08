# Spec — Compose / Export (the convergent half)

**Status:** Draft
**Date:** 2026-06-07
**Related:** `VISION.md` (two halves), `docs/spec.md` (block contract §4), `docs/adr/0001-dotnet-declarative-genui-contract.md`

## Problem

Plexus is strong at the divergent half — EXPLORE: a branching canvas of richly-rendered blocks. The convergent half is missing: there is no way to turn an exploration into a deliverable. Today a user can produce a graph full of charts, tables, and analysis and then has nothing to do with it but screenshot.

This is the missing half of the core thesis (EXPLORE → COMPOSE) and the difference between an exploration toy and a research→deliverable tool. It is reachable now, on the block catalog shipped in C0/C1 — it needs no protocol payload (ADR C2), no media generation, and no multi-provider execution (#1).

## Goals

- Let the user **harvest** selected content from the graph into an ordered deliverable.
- **Export** that deliverable to portable formats, starting with Markdown and PDF.
- Reuse the existing block catalog (charts, tables, code, …) as the renderable units — the deliverable is itself an ordered block list.
- Keep the whole loop reachable on what exists today; no new core dependencies.

## Non-Goals (v1)

- **Not a WYSIWYG document editor.** Compose is harvest + order + light structure, not rich in-place authoring. (Scope; a real editor is a separate, much larger initiative.)
- **Not media generation.** No image/audio/video in the deliverable. (Gated on #1; out of scope.)
- **Not json-render multi-target adoption.** v1 hand-renders to its targets. Adopting json-render's renderers at the export boundary is a separate decision, revisited at X2 if multi-target gets gnarly (see ADR-0001 Option C). (Premature.)
- **Not collaborative or multi-user composition.** (Separate initiative.)
- **No model-authored narrative in the core path.** Optional connective-tissue generation is a later enhancement (X2), kept out of the deterministic core. (Keeps v1 predictable.)

## Design

Three moves. The bridge from EXPLORE to COMPOSE is **selection** (per VISION).

**1. Harvest (selection).** Mark which nodes go into the deliverable. Reuse the existing multi-select (from P2 DAG merge) for ad-hoc harvest; add a lightweight persistent "include" toggle on nodes for building a deliverable up across a session. No new persistence model required for the ad-hoc path.

**2. Compose (assembly).** A COMPOSE view (the second half, distinct from the EXPLORE canvas) that lays the harvested nodes' blocks out as a single ordered list. Reorder and remove; optionally insert Markdown connective blocks (headings, notes) between harvested ones. **The deliverable is itself an ordered block array** — the same contract the canvas already speaks. This is the key reuse: compose = curate a block list; export = render that list to a target.

**3. Export (render to target).** Render the ordered block list to an output format. The block→target mapping is per-renderer. Non-interactive targets (PDF, pptx) need charts rendered to static images — Vega can render headless to SVG/PNG, so a chart block becomes a static image in those targets (or its underlying data as a table fallback).

## Phases

**X0 — Harvest + Markdown (walking skeleton).**
Selection via multi-select → COMPOSE view lists the selected blocks in order → export to a `.md` file.
- [ ] Multi-select on the canvas feeds a COMPOSE view.
- [ ] The view shows the selected nodes' blocks in a single ordered list.
- [ ] Export produces a valid Markdown file: text/tables/code inline; a chart becomes a static image or its data table.
- [ ] Negative: deselecting / empty selection yields an empty-state, not a crash.

**X1 — Ordering + PDF.**
Make the deliverable real: reorder/remove in COMPOSE, add section structure, export to PDF.
- [ ] Reorder and remove blocks in the COMPOSE view; insert Markdown connective blocks.
- [ ] A persistent "include in deliverable" toggle on nodes (build-up across a session).
- [ ] Export to PDF: readable document with charts as rendered images, tables, code, and text.
- [ ] Negative: a deliverable with an unrenderable block degrades (placeholder), does not fail the whole export.

**X2 — More targets + polish.**
- [ ] pptx and/or Remotion export (Remotion connects to the existing interest and is where multi-target pressure may pull in json-render at the boundary — re-evaluate ADR-0001 Option C here).
- [ ] Deliverable theming/templates.
- [ ] (Optional) a model pass that writes connective tissue / intro / transitions over the harvested blocks, turning fragments into a coherent document. Kept out of the core path.

## Open Questions

- **Selection primitive:** is the persistent "include" toggle the same primitive as the brainstormed up/down votes, or separate? (Resolve at X1 — start with multi-select in X0 to avoid deciding early.)
- **Charts in Markdown:** static image embed vs data-table fallback vs both. (Resolve in X0; lean image with a data-table fallback.)
- **COMPOSE surface:** a separate mode/route vs a panel alongside the canvas. (Design call at X0.)
- **Multiple deliverables per graph:** one deliverable per graph for v1, or many? (Lean one for v1; revisit if needed.)

## Parking Lot (good ideas, not in scope)

Model-authored narrative; live/embeddable interactive deliverables; deliverable versioning; direct publish targets (blog/Confluence); collaborative editing.
