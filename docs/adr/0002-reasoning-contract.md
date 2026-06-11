# ADR-0002: Introduce a graph-layer reasoning contract (typed nodes + edges + invariants) with the expert human as a required participant

**Status:** Proposed
**Date:** 2026-06-09
**Deciders:** Carlos Carminati (owner, sole dev)

## Context

ADR-0001 evolved Plexus's per-turn render layer into a .NET-native declarative GenUI catalog and drew an explicit line: *"Surface ≈ node content. The graph/branching model stays outside the render contract, unchanged."* That line left a layer deliberately undefined — the graph itself: what a node *means*, how nodes relate, and what constraints hold across them. Today the graph carries conversation turns; nodes are typed only by their render content (markdown, table, chart, …) and edges are structural (branch/parent) with no semantics.

This is fine for "a better way to converse." It is not enough for the use case identified as Plexus's most native: **decision/investigation → reasoned deliverable.** That use case needs the graph to carry *reasoning structure*, not just conversation: facts with provenance, hypotheses, the evidence weighing between them, and a conclusion that can be traced back to the facts that justify it.

A concrete design target crystallized this — running a disciplined "expert investigator" process on a relatively cheap model:

1. Understand the case → 2. Extract facts → 3. Identify uncertainties → 4. Formulate hypotheses → 5. Contrast hypotheses → 6. Generate conclusion → 7. Explain evidence

— combined with RAG over corporate control catalogs, access to operational APIs, expert worked-examples, and a structured output format. The bet, consistent with where applied agent design is heading: **in narrow domains, process + grounding + tools beat raw model capability**, and the structure (not the model) is the durable asset.

Forces at play:

- The differentiator for Plexus's market  is an **auditable reasoning trail**, not a better answer. The trail is the product; an autonomous black-box answer is *unsignable* in that domain.
- A cheap model only behaves like a disciplined analyst if something *outside the model* enforces the discipline. Prompting alone drifts.
- Per ADR-0001, the contract stays .NET-owned (sidecar owns state and validation). The reasoning layer follows the same ownership.
- Scope risk: a "reasoning layer" can metastasize into a general agent framework. ADR-0001's discipline ("a means, not an end") must carry over.

## Decision

**Introduce a graph-layer reasoning contract** — typed reasoning node roles, a typed edge vocabulary, and cross-node reasoning invariants — as a layer *distinct from and complementary to* the ADR-0001 render catalog. The expert human is a **required participant at defined decision points**, not a reviewer of finished output.

This contract lives where ADR-0001 said the graph lives: **outside the render contract.** ADR-0001 governs what renders *inside* a node; ADR-0002 governs what a node *is* and how nodes relate. The two do not overlap.

This ADR explicitly does **not** authorize building a general reasoning/agent framework. The primitives exist to serve the investigation→deliverable use case in Plexus first. Domain-specific reasoning processes are **recipes (configuration/templates) over the primitives**, never new core types. It also does **not** absorb the compose/export design, which remains its own arc and merely *consumes* this graph.

### Design

**Node roles (graph layer — not GenUI catalog entries).** A node's *role* is reasoning-layer metadata; its *content* still renders via the ADR-0001 catalog.

| Role | Meaning | Renders via (ADR-0001) |
|---|---|---|
| `frame` | The case: question, scope, constraints. Subgraph root. | markdown (+ table) |
| `fact` | Atomic fact, provenance-typed (`source_kind`: doc \| api \| given); `source_ref` required | markdown + link_card |
| `uncertainty` | Gap / unknown / low-confidence flag | markdown (+ badge) |
| `hypothesis` | Candidate explanation. Siblings = the fan-out. | markdown |
| `evaluation` | The contrast: owns the weighing of facts against hypotheses | Vega-Lite (C1) matrix |
| `conclusion` | Selected synthesis | markdown + table |

There is no `explain-evidence` role — explanation is a *projection* over edges (compose traversal), not a node.

**Edge vocabulary (typed, directional).**

| Edge | From → To | Carries |
|---|---|---|
| `grounds` | fact → source (RAG control/bowtie node \| API call) | provenance |
| `addresses` | hypothesis → uncertainty | which gap it would resolve |
| `supports` / `refutes` | fact → hypothesis | `weight` |
| `selects` | conclusion → hypothesis | — |
| `cites` | conclusion → fact | the chain compose renders |

*(Field/edge names are proposed; reconcile with the sidecar's existing discriminator conventions.)*

**Reasoning invariants (cross-node semantic checks — distinct from ADR-0001 schema validation).**

- A `fact` with no valid `grounds` is invalid. ("Data-first, never infer" becomes *enforced*, not aspirational.)
- A `conclusion` that `selects` a hypothesis with net-negative evidence weight raises a flag.
- A `hypothesis` with no `addresses` and no incoming `supports`/`refutes` is dangling (warn).
- Open `uncertainty` nodes (no resolving `addresses`) must surface in the deliverable, not be dropped.

These are the **negative controls** of a recipe, checked in the sidecar over graph structure. They are *not* the same as ADR-0001's schema validation (which checks that a block spec is well-formed against the catalog). Both failure classes feed the router's escalate path, by different mechanisms.

**The recipe (per-domain template, not core).** The "expert investigator" process is the first recipe: a sequence of compose operations over the primitives.

| Step | Emits | Edges |
|---|---|---|
| 1 Understand | 1 `frame` | — |
| 2 Extract facts | N `fact` | `grounds` (each) |
| 3 Uncertainties | M `uncertainty` | — |
| 4 Hypotheses | K `hypothesis` (siblings) | `addresses` |
| 5 Contrast | 1 `evaluation` | `supports`/`refutes` (weighted) |
| 6 Conclusion | 1 `conclusion` | `selects`, `cites` |
| 7 Explain evidence | — (projection) | traverses `cites`→`grounds` |

**Human decision points (first-class, required).** The expert is not a safety net; judgment lives with the expert and the machine is the disciplined executor. The graph records *which* decisions the human made — this is what makes the deliverable attributable and signable.

| Point | Expert action | Why the machine can't own it |
|---|---|---|
| Step 4 fan-out | accept / add / prune hypotheses | domain intuition on what's worth pursuing |
| Step 5 / escalate | accept the weighting, or escalate the `evaluation` to a stronger model | judgment on contested evidence |
| Fact gate | accept / reject a `fact` against ground reality | only the expert knows the terrain |

Escalation is **per-node**: a contested `evaluation` is swapped to a stronger model without re-running the graph — facts and hypotheses stay intact.

### Mapping to the Plexus stack

- **Reasoning contract** = C# types + metadata in the sidecar (same ownership as ADR-0001).
- **Recipes** = data/configuration (per-domain), loaded by the sidecar — not new core types.
- **Invariants** = sidecar validation over graph structure; failures feed the existing router/escalate.
- **Node content** = renders via the ADR-0001 catalog (no change to the render contract).
- **`evaluation` render** = first real driver for a C1 matrix/heatmap data shape (see Consequences).
- **Compose** = graph traversal (`conclusion`→`cites`→`fact`→`grounds`) + auto-generated "limitations / open questions" section from unresolved `uncertainty`. Detailed design stays in the compose/export arc.

## Options Considered

### Option A: Graph-layer reasoning contract, separate from GenUI (CHOSEN)

| Dimension | Assessment |
|---|---|
| Complexity | Medium — node/edge typing + invariants + recipe loader |
| Architecture fit | High — extends the graph layer ADR-0001 left open; stays .NET-owned |
| Differentiation | High — auditable, signable reasoning trail; model-agnostic |
| Scope risk | Medium — bounded by "recipes are config, not core" |

**Pros:** turns the graph into a reasoning artifact; makes the trail auditable/signable; keeps the render contract generic; invariants make small-model discipline enforceable; per-node escalation; clean complement to ADR-0001.
**Cons:** real ongoing design; the recipe/UX must stay low-friction or the human-in-the-loop property collapses to rubber-stamping; introduces a second validation surface.

### Option B: Reasoning steps as GenUI catalog block types

| Dimension | Assessment |
|---|---|
| Architecture fit | Low — violates "graph stays outside the render contract" |
| Differentiation | Low — couples domain method into the render layer |

**Pros:** one layer; simpler mental model.
**Cons:** directly contradicts ADR-0001's surface/graph separation; ossifies the catalog per-domain (the treadmill ADR-0001 just killed, reborn semantically); entangles render and reasoning concerns. *(This was the initial instinct; rejected on re-reading ADR-0001.)*

### Option C: Prompt-only recipe, untyped graph

| Dimension | Assessment |
|---|---|
| Complexity | Low — recipe lives in prompt text only |
| Differentiation | Low — no auditability, no enforceable invariants |

**Pros:** near-zero new architecture; fastest to a demo.
**Cons:** the differentiator evaporates — you cannot query which fact grounded which hypothesis; provenance stays aspirational; small models drift with nothing to validate against; escalation can't be surgical. You keep a nicer chat UI; you do not get the reasoning-instrument transformation.

## Trade-off Analysis

B collapses two concerns into one layer and is rejected for the same reason ADR-0001 rejected inverting the contract into JS: it cedes a separation that was deliberate and load-bearing. C is the "do nothing structural" baseline; it is the cheapest and is exactly what most chat tools already are — which is why it forfeits the only defensible moat (structure, not model). A is more work than C and a cleaner architecture than B: it places the reasoning contract where the graph already lives, keeps the render contract generic, and makes the auditable trail — the thing the regulated market actually buys — a structural property rather than a prompt-time hope.

An external orchestration/agent framework (LangGraph et al.) was not separately evaluated: ADR-0001 already settled that the product-defining contract lives in .NET and the sidecar owns state; the same reasoning rejects pulling the reasoning layer into a JS/Python framework.

The decisive question — should reasoning structure be a *typed, validated graph concern* or a *prompt-time convention* — is answered **typed graph concern**, because auditability and signability cannot be retrofitted onto an untyped graph.

## Consequences

**Easier**

- The deliverable becomes attributable: the graph records which decisions the expert made → signable in CGR / Ley 20.393 contexts.
- "Data-first, never infer" is enforced by invariant, not by prompt discipline.
- A cheap model can run the recipe reliably (narrow typed slots) and escalate per-node.
- Compose has a concrete contract to traverse; the "limitations" section comes for free from open uncertainties.

**Harder**

- A second validation surface (invariants) joins ADR-0001 schema validation in the sidecar; both must feed escalate coherently.
- The `evaluation` render needs a 2D + weight (matrix/heatmap) data shape — beyond C1's `xLabels + series[0]` pie reuse. This is a genuine new catalog requirement (a good treadmill-test, not free).
- The human-in-the-loop property is only real if branch / escalate / fact-gate affordances are near-zero friction. If reviewing the graph is tedious, the expert rubber-stamps and the property is lost. Recorded here as a UX commitment / risk.

**Revisit**

- Promote a recipe from config to a shipped per-domain template once the investigator recipe is validated on a real case.
- Interactive decision points (fact gate, hypothesis pruning) may need mcp_ui / M1 interactivity (and eventually CRDT) — defer until the read-only trail is proven.
- Generalization of the primitives beyond investigation→deliverable — only if earned by serving Plexus first.

## Action Items (incremental, product-driven)

1. [x] **R0 — Node-role + edge metadata.** Add reasoning-role and typed-edge metadata to the graph model in the sidecar, no behavior change. Render unaffected (content still via the catalog). _(Landed: `fd0cc63`. Node reasoning + fact provenance persist via a `reasoning_json` column; typed edges round-trip over JSON/wire — semantic-edge SQLite persistence deferred to R2, see below.)_
2. [x] **R1 — Reasoning invariants.** Implement the invariant checks (fact provenance, net-negative selection, dangling hypothesis, surfaced uncertainties) in the sidecar as a separate `ReasoningGraphValidator`; expose results for escalate. Acceptance includes negative controls (an orphan `fact` must fail; a net-negative `selects` must flag). _(Landed: `9d315b6`. Provenance keys on the persisted `source_ref`, not the deferred `grounds` edge; net-zero evidence is allowed (≥ 0). Escalate was exposed-not-wired at R1 (no reasoning-graph producer yet); now wired in the recipe path (`fb68c68`): a net-negative flag re-runs the contested evaluation→conclusion tail with a stronger model, keeping the sound front intact. The user-initiated conversation escalate (`EscalateTurnAsync`) is a separate surface, unchanged. Remaining flag classes — dangling-hypothesis warn, provenance error — don't auto-escalate yet (earlier-step, would cascade).)_
3. [ ] **R2 — Investigator recipe (config).** Encode the 7-step process as a recipe template over the primitives, with one domain's expert worked-examples. Validate on a real case (e.g. a control investigation from a company catalog). **Acceptance criterion (gate, not a footnote) — the consistency model, not just "edges persist":** today edges are derived from `parentId` only, so a reloaded reasoning graph drops its semantic edges. "Edges persist" alone is rejected as ambiguous — it leaves whether `grounds` is *stored* vs *derived* undecided, which is the exact asymmetry R1's provenance rule had to dodge. R2 must resolve persistence **per edge kind**:
   - `grounds` is **derived from `source_ref`** (a persisted node field), the way structural edges derive from `parentId` — it is **not** stored as an independent edge. This is the lane R1 already chose: provenance keys on `source_ref`, so a reloaded `fact` stays grounded with no `grounds` edge in storage.
   - the relational/weighted edges — `addresses`, `supports`, `refutes`, `selects`, `cites` — carry data recoverable from no node field (notably the `supports`/`refutes` weights), so they **must persist and round-trip via SQLite**.

   Acceptance: a reasoning graph saved → reloaded is byte-identical — `grounds` re-derived from `source_ref`, the relational edges loaded from storage — with `ReasoningGraphValidator` yielding the same diagnostics before and after the round-trip.

   **Status — engine + persistence landed; real grounding pending:**
   - [x] **R2.0a — emission machinery.** Factored `JsonSchemaGen` (gen/validate, shared with the render catalog, behavior-neutral) + `SchemaConstrainedEmitter`: emit → validate structurally → bounded auto-fix → explicit error (never markdown). _(Landed: `f34ab46`.)_
   - [x] **R2.0b — recipe engine.** Config-driven `RecipeExecutor` over the primitives; per-step cardinality + config bounds; referential-integrity + weight-range guards (no silent drop); an instrumented, iterable live smoke (`InvestigatorLiveSmoke`). _(Landed: `7a339ab`, `0473693`, `ff5339a`, `694a3ed`, `73eba8e`.)_
   - [x] **R2.1 — relational-edge persistence.** Semantic edges (with weights) persist + round-trip via SQLite per the consistency model above; gate verified (a net-negative graph reloads with identical R1 diagnostics, which requires the weights to survive). _(Landed: `9a14865`.)_
   - [x] **R2.2.0 — grounding by RESOLUTION (mock).** Retrieval-step + `IFactSource` (curated mock corpus) + source nodes; the facts step cites a retrieved source id, a grounding check rejects an un-retrieved ref, and the `grounds` edge is derived (not stored). _(Landed: `ce71050`.)_ **Honest scope: this is resolution, not yet auditable grounding** — the `source_ref` resolves to a real source, but nothing yet checks that the fact's CLAIM is actually supported by that source (a model can cite a real passage and still launder an invented claim).
   - [x] **R2.2.0-fidelity — claim ⊆ source check.** _(Landed: `fbd27f0`.)_ A separate `IFidelityJudge` verifies the claim is SUPPORTED by the cited source, not merely that the ref resolves — blocking laundering. Two layers in the auto-fix loop: resolution (sync) then fidelity (async judge). Negative control: a laundered claim is re-prompted then corrected; persistent laundering fails the step explicitly. Real judge = an LLM entailment call (`LlmFidelityJudge`), stub in tests. The grounded smoke on a well-matched case showed the model citing faithfully (no laundering), with the resolution check still firing occasionally — so fidelity is a guard for the harder/real-catalog case, now in place. **This makes the grounding auditable, against the mock corpus** (real catalog = R2.2.1).
   - [~] **R2.2.1 — real grounding + worked-examples.** _(Mechanism landed: `4f63698`.)_ `McpFactSource` is the MCP-backed `IFactSource`, swapping in for the mock behind the same interface; the attributable retry counters are in place to read whether real data lowers mis-citations vs over-claims. **Pending (data/infra, not code):** (a) the control/bowtie catalog + operational APIs stood up as an MCP server matching the tool contract ([{ id, text, kind }] with stable ids); (b) a way to RUN a recipe against it on a real case — today the recipe engine is invoked only by the smoke harness, not wired into the app (see below). Then: validate on a real CMP/Collahuasi control investigation.
   - [ ] **Rx — recipe execution entry point.** The recipe engine produces graphs but isn't reachable from the running product (no WS event / UI triggers a run; only tests + the live smoke do). Wiring recipe execution into the app — and surfacing its instrumentation + the human decision seams — is what makes the reasoning engine usable, not library-only. Prereq for the R2.2.1 real-case validation outside the harness.
4. [ ] **R3 — `evaluation` render.** Add the hypothesis × evidence matrix as a C1 catalog data shape (depends on C1).
5. [ ] **R4 — Compose traversal.** Define the `conclusion`→`cites`→`grounds` traversal + limitations projection as the X1 compose contract (in the compose/export spec, consuming this graph).

## Related

- ADR-0001 — GenUI render contract (the layer this complements)
- `docs/spec.md` — block contract (§4); graph/branching model
- compose/export spec — X1 (consumes this graph)
- `docs/spec-mcp-host.md` — mcp_ui / M1 (interactive decision points)
- `VISION.md` — product north-star
