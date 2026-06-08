# Spec — Compose / Export (the convergent half)

**Status:** Draft (rev. 2 — synthesis-centered)
**Date:** 2026-06-08
**Related:** `VISION.md` (two halves), `docs/spec.md` (block contract §4), `docs/spec-mcp-host.md` (grounding via MCP), `docs/adr/0001-dotnet-declarative-genui-contract.md`

## Problem

Plexus is strong at the divergent half — EXPLORE: a branching canvas of richly-rendered blocks. The convergent half is missing.

A first cut (raw harvest → concatenate selected blocks → export) revealed the real shape of the problem: concatenating blocks from unrelated nodes is a *dump*, not a deliverable. The value is not export — it is **converging a coherent exploration into a reasoned deliverable**. That is synthesis, not concatenation.

This is reachable now, on the block catalog (C0/C1) and the MCP host (M0). It needs no protocol payload (ADR C2), no media, no multi-provider execution (#1), and — importantly — **no specialized models**.

## Target use case (the showcase)

**The decisions a developer faces day to day:** choosing a library, a design/UX approach, an architecture, a tool. Each is branching-native (explore the options in parallel) and converges to a reasoned call. Plexus's deliverable for these is a **decision brief** — the same shape as an ADR.

- **Hero demo:** *library X vs Y → recommendation*, grounded by Context7 (current, version-specific library docs). The cleanest and most reproducible — anyone runs it with a stock API key + a connected MCP.
- **Generalizes (same pattern, second example):** a UX/design decision grounded by a design-systems MCP (W3C/WCAG/ARIA + design systems) or web; an architecture decision; a tooling choice. Shown to prove the pattern generalizes — *not* a second build.

The selling point is the **pattern** — branch → ground → synthesize → decide — not any single integration. The "wow" is process/UX (a branching exploration + synthesis yields a more rigorous deliverable than a linear chat, on the same model), never model magic.

## Goals

- Converge a coherent exploration into a reasoned, structured deliverable (a decision brief), not a concatenation.
- Ground the deliverable in current facts via the existing MCP host — **tool-agnostic**.
- Reuse the block catalog (the deliverable is itself a block array) and X0's harvest + export.
- Stay reproducible: runs on a general model + connected tools. No specialized models.

## Non-Goals (v1)

- **Not a WYSIWYG document editor.** Compose is harvest + synthesize + light edit, not rich in-place authoring. (Scope.)
- **Not coupled to any specific MCP.** Grounding is pluggable; `web_search` is the universal floor so the showcase is never blocked on a niche MCP existing. (Robustness.)
- **No specialized / fine-tuned models.** The value is the pattern, not the model; specialization drags in #1 and kills demo reproducibility. (Reproducibility.)
- **Not media generation; not json-render multi-target adoption.** (Gated / premature; the latter is the X2 re-eval point per ADR-0001 Option C.)
- **Not collaborative / multi-user composition.** (Separate initiative.)

## Design

COMPOSE has **two modes**:

1. **Raw harvest (X0 — done):** ad-hoc — select nodes, list their blocks in order, export Markdown. Simple, useful, no synthesis.
2. **Synthesized deliverable (the showcase):** select the option-branches → a **synthesis pass** reads them and emits a structured **decision brief as a block array** — the question · options considered · a comparison table · what was ruled out and why · the recommendation with rationale and caveats · sources. It reads the exploration and *writes the decision*; it does not concatenate.

Key properties:

- The synthesis pass is **a model turn** whose input is the harvested branches + a synthesis instruction, and whose output is deliverable blocks — **reusing the C0/C1 catalog and block emission** (the brief is blocks, validated by the catalog). No new emission machinery.
- **Grounding happens during exploration** (the branches already carry grounded facts from Context7 / design-systems-mcp / web). The synthesis works over already-grounded content, so it is **grounding-agnostic** — it does not know or care which tool grounded a branch.
- The deliverable shape ≈ a mini-ADR. Full circle with how this project's own decisions are documented.

## Phases

- **X0 — Harvest + Markdown (DONE).** The raw/ad-hoc mode: multi-select → COMPOSE drawer lists blocks in order → Markdown export.

- **X1 — Synthesis: branches → decision brief (the showcase build).**
  - [ ] A "synthesize" action over a harvested selection runs a model turn that emits a structured decision-brief block array (question, options, comparison table, ruled-outs, recommendation + rationale + caveats, sources).
  - [ ] The brief renders in the COMPOSE view and exports via X0's Markdown path.
  - [ ] Grounding-agnostic: works over branches grounded by any connected MCP **or** web-only — no specific MCP required.
  - [ ] Quality bar (showcase): on a real "library X vs Y" exploration grounded by Context7, the brief is credible and current (reflects actual current library capabilities) and is demonstrably better-reasoned than a single linear prompt.
  - [ ] Negative: synthesis over incoherent/unrelated branches produces a brief that reflects what is actually there (or notes the absence of a clear decision), and never crashes.

- **X2 — Export targets + theming.** PDF, pptx, Remotion; deliverable theming. (Demoted from the earlier draft — not needed for the showcase, since Markdown export suffices for the demo. Remotion/multi-target is where ADR-0001 Option C, json-render at the export boundary, gets re-evaluated.)

- **X3 — More deliverable types / polish.** Other brief shapes (postmortem/RCA, comparison report); a persistent "include" toggle; optional richer synthesis.

## Open Questions

- **Synthesis input:** infer the options from harvested content (v1) vs explicit option/branch tagging (later)? (Lean: infer for v1.)
- **Deliverable placement:** does the synthesized brief become a graph node (re-explorable / escalatable) or a separate compose artifact? (Lean: a node is elegant — it is the convergent node — but defer.)
- **Grounding floor confirmed:** X1 must pass with web-only grounding (no required MCP). Context7 / design-systems-mcp are enhancements, not prerequisites.

## Parking Lot

Persistent "include" toggle; model-suggested branch selection; deliverable versioning; direct publish targets (blog/Confluence); collaborative editing; interactive/embeddable deliverables.
