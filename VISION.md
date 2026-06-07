# Plexus — Vision

## The thesis

Plexus is a workspace where **divergent exploration on a branching canvas converges into a deliverable.** It is not a better chat — it is the place where you think through a topic, generate artifacts as you go, and harvest the good ones into something you can share.

## Why a graph, not a chat

Real research and thinking are divergent and iterative: you branch, compare options, backtrack, and keep several threads alive at once. A linear chat collapses all of that into one column and loses the shape. Plexus keeps the exploration legible and navigable as a graph of richly-rendered blocks — each answer shown in its best form (a table, a chart, a link card, code) instead of a wall of text.

## Two halves

**Explore** (divergent). Branch from any node and resume with the context up to that point. Run the same input across models and compare side by side. Pull in context and act through MCP tools. Generate typed artifacts — and, later, media. The canvas is for going wide.

**Compose** (convergent). Mark the artifacts worth keeping, harvest them into an ordered deliverable, and export — markdown, PDF, slides, eventually video. This half is for converging.

The bridge between them is **selection**: the marks you leave while exploring are exactly what gets harvested into the deliverable.

## Principles

- **Local-first.** Your graph and your keys stay on your machine. Bring your own API key.
- **Cost and quality, made legible and controllable.** Every answer shows its model and cost. Routing lets you trade off cost against quality — manually, or automatically by request complexity.
- **Provider-agnostic by design.** The registry and routing are model-agnostic; execution is Anthropic-first today (see issue #1).
- **Honest about its own limits.** Divergences from spec and known gaps are documented and tracked, not hidden.

## Where it's heading

- **Today:** branching canvas, rich adaptive blocks, cost-aware model routing, MCP tools with a human gate, conversation history.
- **Next:** richer in-conversation UI from MCP servers, a selection/marking layer, and the compose/export surface — the convergent half.
- **Horizon:** multi-provider execution, which unlocks media generation (image → audio → video), with composition tools like Remotion as one export target for finished pieces.

The phased roadmap lives in `docs/spec*.md`. This file is the north star — it changes rarely; the specs change often.
