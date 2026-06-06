# Plexus — Feature Spec: Model Routing

> **Addendum to [spec.md](./spec.md).** This is a separate, orthogonal track — it does not depend on P2 (MCP / json-render) and can land before or alongside it.
>
> Goal: let the user configure a list of providers/models and choose one **manually** or **automatically by request complexity**, optimizing for cost or efficiency.

---

## 0. Where this slots into the existing roadmap

Assumes **P0 + P1 are done** (block contract, canvas, branching, resume-from-node, prompt caching, SQLite). Those satisfy the prerequisites: a working provider call path, persistence, and caching.

Sequencing principle: **introduce the seam now, the intelligence incrementally.**

| Phase | When | What | Risk |
|-------|------|------|------|
| **R0** | Now (next slice after P1) | Registry + `IModelRouter` interface + `ManualRouter` + per-node model/cost display | Low |
| **R1** | After R0, once cost is visible | `HeuristicRouter`: capability-filter → rule-based tiering + policy toggle | Medium |
| **R2** | Gated (see §3) | Learned router (RouteLLM-class) **or** external gateway, behind the same interface | High / optional |

**Do not** start R1 before R0's telemetry exists — you cannot validate auto-routing without seeing per-request cost. **Do not** start R2 unless R1's measured data shows a learned router would pay for its added complexity.

## 1. The provider/model registry

Do **not** hand-maintain pricing or capabilities. Source metadata from [models.dev](https://models.dev) (open DB, `models.dev/api.json`, 75+ providers, AI-SDK-aligned model IDs; it's what OpenCode uses). [LiteLLM's price/context JSON](https://github.com/BerriAI/litellm) is an equivalent fallback dataset.

```ts
// The registry = user-configured providers  ×  metadata pulled from models.dev
export interface ProviderConfig {
  id: string;                 // "anthropic", "openai", "ollama", ...
  baseUrl?: string;           // for self-hosted / gateways / Ollama
  // API key is NOT here — it lives in the OS keychain, referenced by provider id
  enabled: boolean;
}

export interface ModelMetadata {  // mirror of the fields we consume from models.dev
  id: string;                 // "claude-haiku-4-5", "gpt-5", ...
  providerId: string;
  costInPerMTok: number;
  costOutPerMTok: number;
  contextWindow: number;
  maxOutput: number;
  capabilities: {
    toolCall: boolean;
    structuredOutput: boolean;
    reasoning: boolean;
    vision: boolean;          // input modalities include image
  };
}
```

Refresh `ModelMetadata` from models.dev on a schedule (and cache locally) so prices/limits stay current without code changes.

## 2. The routing seam

```ts
export type RoutingPolicy =
  | { kind: "manual"; modelId: string }
  | { kind: "auto"; objective: "cost" | "quality" | "balanced"; budgetPerTurn?: number };

export interface RoutingContext {
  messages: SerializedTurn[];     // the reconstructed ancestor path (spec.md §4.4)
  requires: {                     // hard requirements derived from the request
    toolCall?: boolean;
    structuredOutput?: boolean;   // we always need this for block emission strategy (a)
    vision?: boolean;
    minContext?: number;          // tokens needed for the reconstructed history
  };
  policy: RoutingPolicy;
}

export interface ModelChoice {
  modelId: string;
  providerId: string;
  reason: string;                 // human-readable: "auto/cost: cheapest capable model"
}

export interface IModelRouter {
  selectModel(ctx: RoutingContext): Promise<ModelChoice>;
}
```

**The two-step every router follows:**

1. **Filter by hard capability.** Drop candidates that can't satisfy `requires` (no tool-calling, no structured output, context too small, no vision). Use the models.dev flags. This prevents picking a cheap model that *cannot do the task*.
2. **Optimize by policy within survivors.** `cost` → cheapest; `quality` → highest-capability; `balanced` → best quality under `budgetPerTurn`.

Implementations, all behind `IModelRouter`:
- `ManualRouter` (R0) — returns `policy.modelId`, still runs the capability filter as a guardrail (warn if the manually chosen model can't meet `requires`).
- `HeuristicRouter` (R1) — rule-based tiering (§3.R1).
- `LearnedRouter` / `GatewayRouter` (R2) — pluggable.

Each provider is a `Microsoft.Extensions.AI.IChatClient`; the router only decides which one to invoke.

## 3. Phases & acceptance criteria

### R0 — Registry + manual + telemetry — ✅ Done (v0.3.0)
- [x] Providers configurable; API keys in OS keychain, referenced by provider id.
- [x] Model metadata pulled and cached from models.dev; refreshes on a schedule.
- [x] `IModelRouter` interface exists; `ManualRouter` implemented.
- [x] Choice stored in `node.meta`. **The model-selector UI moved to R1** — it ended up implemented as the unified policy control (`PolicyPicker`), which covers the per-session default + per-node override for *both* manual model selection and the auto modes. R0 shipped the data path (`node.meta`); the picker UI is R1.
- [x] **Telemetry**: every call logs `{ modelId, providerId, tokensIn, tokensOut, cost, latencyMs, policy, reason }`.
- [x] Each node displays a model badge + its cost.

### R1 — Heuristic auto-routing — ✅ Done (v0.4.0)
- [x] `RoutingPolicy` toggle in the UI: Manual / Auto-cost / Auto-quality / Auto-balanced. *Implemented as the unified `PolicyPicker`, which also absorbs R0's deferred manual model selector (one control for session default + per-node override).*
- [x] `HeuristicRouter` implements capability-filter → tiering. Signals → tier (small / mid / large):
  - prompt + reconstructed-history length
  - presence of code / attachments
  - `requires.toolCall` or `requires.structuredOutput`
  - conversation depth (ancestor count)
- [x] Manual override always wins over auto.
- [x] `ModelChoice.reason` surfaced in the node badge (so the user sees *why* a model was picked).
- [x] Acceptance test: given a trivial prompt under Auto-cost, the cheapest capable model is selected; given a prompt requiring tools, models lacking tool-calling are never selected.

> **Note on candidates (resolves §5 open question):** the auto-router chooses from a **curated per-provider {small, mid, large} table**, not the full models.dev catalog; models.dev is metadata-only.
> **Reported divergence (not changed):** §2 says "each provider is an `IChatClient`; the router only decides which one to invoke." The routing *seam* and selection are done, but turn **execution is still Anthropic-only** — multi-provider `IChatClient` dispatch is deferred (effectively R2-adjacent). §4.1's cache-aware cost (accounting for cached-prefix reuse when scoring) is also not yet implemented.

### R2 — Learned router or gateway (gated) — ⏳ Planned (gated)
**Gate condition:** R1 telemetry shows meaningful spend AND evidence that the heuristic mis-routes often enough to matter. Only then:
- [ ] Either integrate a learned router ([RouteLLM](https://github.com/lm-sys/RouteLLM), [Anyscale llm-router](https://github.com/anyscale/llm-router), or [LLMRouter](https://github.com/ulab-uiuc/LLMRouter)) behind `IModelRouter`,
- [ ] **or** add a `GatewayRouter` that delegates to an external gateway (OpenRouter Auto, LiteLLM) — same interface, the gateway does the routing.

## 4. Cross-cutting concerns

### 4.1 Routing fights prompt caching
Cache is per-model/provider. If the router flips models mid-thread, the shared-prefix cache is lost. Therefore:
- **Default to branch-level routing, not per-turn.** A model is chosen when a branch starts and stays sticky for that branch unless `requires` forces a change (e.g. a turn suddenly needs vision).
- When computing `cost` for the policy, account for cache state: re-using a cached prefix on the current model may beat a nominally cheaper model that starts cold.

### 4.2 Canvas integration (this is the differentiator)
- Per-node **model badge + cost**, plus `reason` on hover.
- **"Escalate" action**: re-run a node with a stronger model as a *sibling branch* — one click, both answers visible side by side. Routing + branching turn model comparison into a first-class visual act.
- Auto-routing can surface a soft suggestion: "Auto picked Haiku here — escalate to Opus?" → creates the sibling branch.

## 5. Open questions
- Tier→model mapping per provider: hard-coded table, or derived from a models.dev capability/cost ranking? *(eng)*
- How to estimate `minContext` cheaply before the call (token-count the reconstructed history with which tokenizer)? *(eng)*
- Should the heuristic ever *down*-route mid-thread to save cost, accepting the cache loss? Probably no by default. *(eng)*

## References
- models.dev — open model database: https://models.dev
- RouteLLM (LMSYS) — learned routing on preference data: https://github.com/lm-sys/RouteLLM
- vLLM Semantic Router — ModernBERT complexity classifier: https://github.com/vllm-project/semantic-router
- LiteLLM — gateway + price/context dataset: https://github.com/BerriAI/litellm
