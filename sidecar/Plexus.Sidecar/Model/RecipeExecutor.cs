using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.Extensions.AI;
using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Services;

namespace Plexus.Sidecar.Model;

// ADR-0002 R2.0b — the recipe engine (walking skeleton). Runs a declarative Recipe
// over a provider, step by step: build the step's schema FROM CONFIG (cardinality +
// bounds — never engine constants), emit through the schema-constrained auto-fix loop,
// then assemble typed reasoning nodes + edges from the emission content. The output is
// a reasoning graph the R1 ReasoningGraphValidator can grade — the engine's oracle.
//
// There is NO hardcoded step logic: the executor is driven entirely by Recipe.Steps.
// `grounds` edges are NOT created — provenance is derived from a fact's source_ref
// (the R1 decision), so a reloaded fact stays grounded with no grounds edge stored.
// Decision seams fire an optional callback (auto-resolved when none is supplied; the
// interactive UI is M1).
public sealed record RecipeRunResult(
    bool Ok,
    Graph Graph,
    string? Error = null,
    string? FailedStepId = null,
    IReadOnlyList<StepReport>? Steps = null,
    // ADR-0002 escalate: step ids re-run with a stronger model after R1 flagged the
    // small-model output (empty when nothing escalated). Per-node — the sound front
    // (facts/hypotheses) is kept; only the contested tail is re-run.
    IReadOnlyList<string>? EscalatedSteps = null);

// Per-step instrumentation for the live smoke: how many auto-fix retries each step
// needed, split by failure kind (structural shape vs referential bad-ref), and whether
// it exhausted. This is what tells whether the small-model thesis holds — which steps
// to escalate by default, which prompts/bounds to tune — not just pass/fail.
public sealed record StepReport(
    string StepId,
    string Role,
    bool Ok,
    int Attempts,
    int StructuralFailures,
    // The post-structural retries, attributed (was one conflated counter). Their sum is
    // the total post-structural retries for the step — no double-count, no loss.
    int ResolutionRetries,
    int FidelityRetries,
    int ReferentialRetries);

public static class RecipeExecutor
{
    public static async Task<RecipeRunResult> RunAsync(
        IChatClient client,
        Recipe recipe,
        string modelId,
        string? context = null, // the case/material under investigation, shared by every step
        string? escalateModelId = null, // stronger model to re-run the contested tail on an R1 flag
        IFactSource? factSource = null, // R2.2: grounds facts in retrieved sources (null = ungrounded)
        IFidelityJudge? fidelityJudge = null, // R2.2.0-fidelity: claim ⊆ source check (null = resolution only)
        Func<RecipeStep, Graph, CancellationToken, Task>? onDecisionSeam = null,
        int maxAttempts = 3,
        CancellationToken ct = default)
    {
        var state = new RunState();
        var reports = new List<StepReport>();

        // R2.2 retrieval-step: pull sources for the case up front and add them as source
        // nodes, so the facts step can ground each fact in a retrieved passage (cite its
        // id) instead of inventing source_refs. Deterministic retrieval; the model only
        // extracts + cites.
        if (factSource is not null)
            foreach (var passage in await factSource.RetrieveAsync(context ?? "", ct))
                state.AddSourceNode(passage);

        foreach (var step in recipe.Steps)
        {
            var schema = SchemaForStep(step);
            var instruction = BuildInstruction(step, state, context);
            var result = await SchemaConstrainedEmitter.EmitAsync(
                client, modelId, instruction, schema, maxAttempts,
                postStructuralCheck: RefCheckFor(step, state, fidelityJudge), ct: ct);

            reports.Add(ReportFor(step.Id, step.Role, result));

            // A structured emission that never validates must surface explicitly — it
            // can NOT degrade to free text without breaking the typed-graph contract.
            if (!result.Ok || result.Value is null)
                return new RecipeRunResult(false, state.Graph, result.Error ?? "emission failed", step.Id, reports);

            ApplyStep(step, result.Value, state);

            if (step.DecisionSeam && onDecisionSeam is not null)
                await onDecisionSeam(step, state.Graph, ct);
        }

        var escalated = escalateModelId is null
            ? Array.Empty<string>()
            : await EscalateContestedAsync(client, recipe, escalateModelId, context, state, reports, fidelityJudge, maxAttempts, ct);

        return new RecipeRunResult(true, state.Graph, Steps: reports, EscalatedSteps: escalated);
    }

    // ADR-0002 per-node escalate: when R1 flags the small-model graph, re-run the
    // CONTESTED tail with a stronger model, keeping the sound front intact. A
    // net-negative selection contests the EVALUATION (the weighing) — so we re-run from
    // the evaluation step onward (evaluation → conclusion), re-weighing the evidence and
    // re-concluding, while facts/uncertainties/hypotheses stay put. Returns the step ids
    // re-run (empty if nothing was flagged).
    private static async Task<IReadOnlyList<string>> EscalateContestedAsync(
        IChatClient client, Recipe recipe, string escalateModelId, string? context,
        RunState state, List<StepReport> reports, IFidelityJudge? fidelityJudge, int maxAttempts, CancellationToken ct)
    {
        var v = ReasoningGraphValidator.Validate(state.Graph);
        if (!v.Diagnostics.Any(d => d.Code == ReasoningDiagnosticCodes.ConclusionNetNegative))
            return Array.Empty<string>();

        var from = recipe.Steps.FindIndex(s => s.Role == ReasoningRoles.Evaluation);
        if (from < 0)
            return Array.Empty<string>();

        var tail = recipe.Steps.Skip(from).ToList();
        state.RemoveByRoles(tail.Select(s => s.Role).ToHashSet());

        var escalated = new List<string>();
        foreach (var step in tail)
        {
            var schema = SchemaForStep(step);
            var instruction = BuildInstruction(step, state, context);
            var result = await SchemaConstrainedEmitter.EmitAsync(
                client, escalateModelId, instruction, schema, maxAttempts,
                postStructuralCheck: RefCheckFor(step, state, fidelityJudge), ct: ct);

            reports.Add(ReportFor($"{step.Id}*escalated", step.Role, result));
            if (!result.Ok || result.Value is null)
                break; // escalation failed to emit; leave what we have, record what we tried

            ApplyStep(step, result.Value, state);
            escalated.Add(step.Id);
        }
        return escalated;
    }

    // The model instruction = the step's (config) prompt + the dynamic ref context a
    // ref-carrying step needs (the available facts/hypotheses/uncertainties, by id), so
    // the model can reference real refs instead of inventing them.
    private static string BuildInstruction(RecipeStep step, RunState state, string? context)
    {
        var head = string.IsNullOrWhiteSpace(context) ? "" : $"{context}\n\n";

        // R2.2 grounding: the facts step lists the retrieved sources so the model sets
        // each fact's sourceRef to a REAL source id (verifiable provenance), not invent it.
        if (step.Role == ReasoningRoles.Fact && state.Sources.Count > 0)
        {
            var g = new StringBuilder(head + step.Prompt);
            g.Append("\n\nGround each fact in one of these sources — set its sourceRef to the exact id:");
            foreach (var (id, text) in state.Sources)
                g.Append($"\n- {id}: {Truncate(text, 160)}");
            return g.ToString();
        }

        var prefixes = step.Role switch
        {
            ReasoningRoles.Hypothesis => new[] { "u" },
            ReasoningRoles.Evaluation => new[] { "f", "h" },
            ReasoningRoles.Conclusion => new[] { "h", "f" },
            _ => Array.Empty<string>(),
        };
        if (prefixes.Length == 0)
            return head + step.Prompt;

        var sb = new StringBuilder(head + step.Prompt);
        sb.Append("\n\nReference these by their exact id:");
        var any = false;
        foreach (var p in prefixes)
            foreach (var (rf, _, text) in state.Refs.Where(r => r.Prefix == p))
            {
                sb.Append($"\n- {rf}: {Truncate(text, 120)}");
                any = true;
            }
        return any ? sb.ToString() : step.Prompt;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // Per-step schema. Array steps get a bounded envelope with the step's own min/max
    // (config, not a constant); single steps get the primitive's object schema
    // (Evaluation's bounds the weight to [0,1]).
    private static JsonSchema SchemaForStep(RecipeStep step)
    {
        if (step.Array)
        {
            var (itemType, key) = PrimitiveFor(step.Role);
            return ReasoningSchemas.BoundedArrayEnvelope(key, itemType, step.MinItems, step.MaxItems);
        }
        return step.Role switch
        {
            ReasoningRoles.Frame => ReasoningSchemas.Frame,
            ReasoningRoles.Evaluation => ReasoningSchemas.Evaluation,
            ReasoningRoles.Conclusion => ReasoningSchemas.Conclusion,
            _ => throw new ArgumentException($"No single-node schema for role '{step.Role}'", nameof(step)),
        };
    }

    // Referential integrity (run inside the emission loop, after structural validation):
    // every ref a step emits (hypothesis→uncertainty, weighing→fact/hypothesis,
    // conclusion→hypothesis/facts) must resolve to a node created by an earlier step.
    // A real model sometimes references a ref that doesn't exist; rather than silently
    // drop the edge (which would hide the model's error and lose information — fatal in
    // an auditability tool), we feed it back to the auto-fix loop, then fail explicitly.
    private static Func<JsonNode, CancellationToken, Task<PostStructuralFinding>>? RefCheckFor(
        RecipeStep step, RunState state, IFidelityJudge? fidelityJudge)
    {
        // Grounded facts: two layers. RESOLUTION — the sourceRef must cite a retrieved
        // source (sync, cheap). FIDELITY — the claim must be SUPPORTED by that source, not
        // merely cite it (async, the judge); this is what blocks laundering. Both feed the
        // same auto-fix loop, but are attributed to DISTINCT categories so a retry says
        // which one (a mis-citation vs an over-claim).
        if (step.Role == ReasoningRoles.Fact && state.Sources.Count > 0)
            return (json, ct) => GroundedFactsCheckAsync(json, state, fidelityJudge, ct);

        return step.Role is ReasoningRoles.Hypothesis or ReasoningRoles.Evaluation or ReasoningRoles.Conclusion
            ? (json, _) => Task.FromResult(new PostStructuralFinding(Category.Referential, UnresolvedRefs(step.Role, json, state.RefToId)))
            : null;
    }

    private static async Task<PostStructuralFinding> GroundedFactsCheckAsync(
        JsonNode json, RunState state, IFidelityJudge? judge, CancellationToken ct)
    {
        var resolution = UngroundedFacts(json, state.SourceIds);
        if (resolution.Count > 0 || judge is null)
            return new PostStructuralFinding(Category.Resolution, resolution); // gate fidelity until every ref resolves

        var bad = new List<string>();
        foreach (var f in json["facts"]?.AsArray() ?? new JsonArray())
        {
            var claim = (string?)f?["claim"];
            var sref = (string?)f?["sourceRef"];
            if (string.IsNullOrEmpty(claim) || sref is null)
                continue;
            var sourceText = state.SourceTextOf(sref);
            if (sourceText is not null && !await judge.IsSupportedAsync(claim, sourceText, ct))
                bad.Add($"the claim \"{Truncate(claim, 60)}\" is not supported by source '{sref}' — cite a source that supports it, or drop the claim");
        }
        return new PostStructuralFinding(Category.Fidelity, bad);
    }

    // Post-structural retry categories — what a grounding/ref retry was actually about.
    private static class Category
    {
        public const string Resolution = "resolution"; // sourceRef doesn't resolve to a retrieved source (mis-citation)
        public const string Fidelity = "fidelity";     // sourceRef resolves but the claim isn't supported (over-claim)
        public const string Referential = "referential"; // an emitted ref (f0/h0/u0) doesn't resolve in the set (R2.0b)
    }

    // Build a step report, attributing the emission's post-structural retries per category.
    private static StepReport ReportFor(string stepId, string role, SchemaEmissionResult r)
    {
        var by = r.PostStructuralByCategory;
        int Cat(string c) => by?.GetValueOrDefault(c) ?? 0;
        return new StepReport(stepId, role, r.Ok, r.Attempts, r.StructuralFailures,
            Cat(Category.Resolution), Cat(Category.Fidelity), Cat(Category.Referential));
    }

    private static IReadOnlyList<string> UngroundedFacts(JsonNode json, IReadOnlySet<string> sourceIds)
    {
        var bad = new List<string>();
        foreach (var f in json["facts"]?.AsArray() ?? new JsonArray())
        {
            var sr = (string?)f?["sourceRef"];
            if (string.IsNullOrEmpty(sr) || !sourceIds.Contains(sr))
                bad.Add(string.IsNullOrEmpty(sr) ? "(empty)" : sr);
        }
        if (bad.Count == 0)
            return Array.Empty<string>();
        var valid = string.Join(", ", sourceIds.OrderBy(x => x, StringComparer.Ordinal));
        return bad.Distinct().Select(sr => $"sourceRef '{sr}' is not a retrieved source (valid: {valid})").ToList();
    }

    private static IReadOnlyList<string> UnresolvedRefs(string role, JsonNode json, IReadOnlyDictionary<string, string> refs)
    {
        var bad = new List<string>();
        void Check(string? r)
        {
            if (!string.IsNullOrEmpty(r) && !refs.ContainsKey(r))
                bad.Add(r);
        }

        switch (role)
        {
            case ReasoningRoles.Hypothesis:
                foreach (var h in json["hypotheses"]?.AsArray() ?? new JsonArray())
                    foreach (var a in h?["addresses"]?.AsArray() ?? new JsonArray())
                        Check((string?)a);
                break;
            case ReasoningRoles.Evaluation:
                foreach (var w in json["weighings"]?.AsArray() ?? new JsonArray())
                {
                    Check((string?)w?["fact"]);
                    Check((string?)w?["hypothesis"]);
                }
                break;
            case ReasoningRoles.Conclusion:
                Check((string?)json["selects"]);
                foreach (var c in json["cites"]?.AsArray() ?? new JsonArray())
                    Check((string?)c);
                break;
        }

        if (bad.Count == 0)
            return Array.Empty<string>();

        var valid = refs.Count == 0 ? "(none yet)" : string.Join(", ", refs.Keys.OrderBy(k => k, StringComparer.Ordinal));
        return bad.Distinct().Select(r => $"reference '{r}' does not exist (valid refs: {valid})").ToList();
    }

    private static (Type ItemType, string ArrayKey) PrimitiveFor(string role) => role switch
    {
        ReasoningRoles.Frame => (typeof(FrameEmission), ""),
        ReasoningRoles.Fact => (typeof(FactEmission), "facts"),
        ReasoningRoles.Uncertainty => (typeof(UncertaintyEmission), "uncertainties"),
        ReasoningRoles.Hypothesis => (typeof(HypothesisEmission), "hypotheses"),
        ReasoningRoles.Evaluation => (typeof(EvaluationEmission), ""),
        ReasoningRoles.Conclusion => (typeof(ConclusionEmission), ""),
        _ => throw new ArgumentException($"Unknown reasoning role '{role}'", nameof(role)),
    };

    private static string RefPrefix(string role) => role switch
    {
        ReasoningRoles.Frame => "frame",
        ReasoningRoles.Fact => "f",
        ReasoningRoles.Uncertainty => "u",
        ReasoningRoles.Hypothesis => "h",
        ReasoningRoles.Evaluation => "e",
        ReasoningRoles.Conclusion => "c",
        _ => "x",
    };

    private static void ApplyStep(RecipeStep step, JsonNode value, RunState s)
    {
        var prefix = RefPrefix(step.Role);
        switch (step.Role)
        {
            case ReasoningRoles.Frame:
            {
                var fe = value.Deserialize<FrameEmission>(PlexusJson.Options)!;
                var text = fe.Scope is null ? fe.Question : $"{fe.Question}\n\n_Scope:_ {fe.Scope}";
                s.Bind(prefix, s.AddNode(ReasoningRoles.Frame, text, new ReasoningMeta { Role = ReasoningRoles.Frame }), text);
                break;
            }
            case ReasoningRoles.Fact:
            {
                foreach (var f in value["facts"]!.Deserialize<List<FactEmission>>(PlexusJson.Options)!)
                {
                    // When grounded, the source kind is authoritative from the matched
                    // source node (the model only cites the id), not the model's guess.
                    var kind = s.SourceIds.Contains(f.SourceRef) ? s.SourceKindOf(f.SourceRef) : f.SourceKind;
                    var fid = s.AddNode(ReasoningRoles.Fact, f.Claim,
                        new ReasoningMeta { Role = ReasoningRoles.Fact, SourceKind = kind, SourceRef = f.SourceRef });
                    s.Bind(prefix, fid, f.Claim);
                    // Derive the grounds edge (fact → source) from a resolving source_ref.
                    if (s.SourceIds.Contains(f.SourceRef))
                        s.Graph.Edges.Add(new Edge { From = fid, To = f.SourceRef, Kind = ReasoningEdges.Grounds });
                }
                break;
            }
            case ReasoningRoles.Uncertainty:
            {
                foreach (var u in value["uncertainties"]!.Deserialize<List<UncertaintyEmission>>(PlexusJson.Options)!)
                    s.Bind(prefix, s.AddNode(ReasoningRoles.Uncertainty, u.Question, new ReasoningMeta { Role = ReasoningRoles.Uncertainty }), u.Question);
                break;
            }
            case ReasoningRoles.Hypothesis:
            {
                foreach (var h in value["hypotheses"]!.Deserialize<List<HypothesisEmission>>(PlexusJson.Options)!)
                {
                    var hid = s.AddNode(ReasoningRoles.Hypothesis, h.Statement, new ReasoningMeta { Role = ReasoningRoles.Hypothesis });
                    s.Bind(prefix, hid, h.Statement);
                    foreach (var uref in h.Addresses ?? Enumerable.Empty<string>())
                        if (s.RefToId.TryGetValue(uref, out var uid))
                            s.Graph.Edges.Add(new Edge { From = hid, To = uid, Kind = ReasoningEdges.Addresses });
                }
                break;
            }
            case ReasoningRoles.Evaluation:
            {
                var ev = value.Deserialize<EvaluationEmission>(PlexusJson.Options)!;
                s.Bind(prefix, s.AddNode(ReasoningRoles.Evaluation, "Evaluation", new ReasoningMeta { Role = ReasoningRoles.Evaluation }), "Evaluation");
                foreach (var w in ev.Weighings)
                    if (s.RefToId.TryGetValue(w.Fact, out var fid) && s.RefToId.TryGetValue(w.Hypothesis, out var hid))
                        s.Graph.Edges.Add(new Edge { From = fid, To = hid, Kind = EdgeKindForStance(w.Stance), Weight = w.Weight });
                break;
            }
            case ReasoningRoles.Conclusion:
            {
                var c = value.Deserialize<ConclusionEmission>(PlexusJson.Options)!;
                var cid = s.AddNode(ReasoningRoles.Conclusion, c.Summary ?? "Conclusion", new ReasoningMeta { Role = ReasoningRoles.Conclusion });
                s.Bind(prefix, cid, c.Summary ?? "Conclusion");
                if (s.RefToId.TryGetValue(c.Selects, out var selId))
                    s.Graph.Edges.Add(new Edge { From = cid, To = selId, Kind = ReasoningEdges.Selects });
                foreach (var cite in c.Cites)
                    if (s.RefToId.TryGetValue(cite, out var citeId))
                        s.Graph.Edges.Add(new Edge { From = cid, To = citeId, Kind = ReasoningEdges.Cites });
                break;
            }
        }
    }

    private static string EdgeKindForStance(string stance) => stance.ToLowerInvariant() switch
    {
        "supports" => ReasoningEdges.Supports,
        "refutes" => ReasoningEdges.Refutes,
        _ => throw new InvalidOperationException($"Unknown weighing stance '{stance}'."),
    };

    // Mutable run state: the graph under construction, the ref→node-id map the later
    // steps resolve against, and the ordered refs (with prefix + text) the prompt
    // builder lists back to the model.
    private sealed class RunState
    {
        public Graph Graph { get; } = new() { Id = "recipe" };
        public Dictionary<string, string> RefToId { get; } = new(StringComparer.Ordinal);
        public List<(string Ref, string Prefix, string Text)> Refs { get; } = new();
        // R2.2 retrieved sources: listed to the facts step (Sources) and used to verify a
        // fact's source_ref resolves (SourceIds) + to set its authoritative kind.
        public List<(string Id, string Text)> Sources { get; } = new();
        public HashSet<string> SourceIds { get; } = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _sourceKind = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _sourceText = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _roleCounters = new(StringComparer.Ordinal);
        private int _seq;
        private string? _frameId;

        // Add a retrieved source as a provenance node whose id IS the passage id, so a
        // fact's source_ref (the same id) derives the grounds edge and round-trips.
        public void AddSourceNode(SourcePassage p)
        {
            Graph.Nodes.Add(new Node
            {
                Id = p.Id,
                ParentId = null, // provenance aux node, not part of the reasoning chain
                Role = "assistant",
                CreatedAt = $"src-{p.Id}",
                Blocks = new List<Block> { new MarkdownBlock { Text = p.Text } },
                Raw = p.Text,
                Reasoning = new ReasoningMeta { Role = ReasoningRoles.Source, SourceKind = p.Kind, SourceRef = p.Id },
            });
            Sources.Add((p.Id, p.Text));
            SourceIds.Add(p.Id);
            _sourceKind[p.Id] = p.Kind;
            _sourceText[p.Id] = p.Text;
        }

        public string? SourceKindOf(string id) => _sourceKind.GetValueOrDefault(id);
        public string? SourceTextOf(string id) => _sourceText.GetValueOrDefault(id);

        public string AddNode(string role, string text, ReasoningMeta reasoning)
        {
            var id = $"n{_seq}";
            Graph.Nodes.Add(new Node
            {
                Id = id,
                ParentId = role == ReasoningRoles.Frame ? null : _frameId,
                Role = "assistant",
                CreatedAt = _seq.ToString("D6"),
                Blocks = new List<Block> { new MarkdownBlock { Text = text } },
                Raw = text,
                Reasoning = reasoning,
            });
            if (role == ReasoningRoles.Frame)
                _frameId = id;
            else if (_frameId is not null)
                Graph.Edges.Add(new Edge { From = _frameId, To = id }); // structural (branch), Kind null
            _seq++;
            return id;
        }

        // Assign the next per-prefix ref (f0, f1, …) to a freshly created node, recording
        // its text so a later step's prompt can list it.
        public void Bind(string prefix, string nodeId, string text)
        {
            var n = _roleCounters.TryGetValue(prefix, out var c) ? c : 0;
            _roleCounters[prefix] = n + 1;
            var rf = $"{prefix}{n}";
            RefToId[rf] = nodeId;
            Refs.Add((rf, prefix, text));
        }

        // Drop all nodes of the given reasoning roles (and any edge touching them, and
        // their refs), so an escalation pass can re-run those steps from scratch. The
        // sound front (other roles) is untouched.
        public void RemoveByRoles(IReadOnlySet<string> roles)
        {
            var removedIds = Graph.Nodes.Where(n => n.Reasoning?.Role is { } r && roles.Contains(r))
                .Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            Graph.Nodes.RemoveAll(n => removedIds.Contains(n.Id));
            Graph.Edges.RemoveAll(e => removedIds.Contains(e.From) || removedIds.Contains(e.To));

            var prefixes = roles.Select(RefPrefix).ToHashSet(StringComparer.Ordinal);
            foreach (var (rf, prefix, _) in Refs.Where(r => prefixes.Contains(r.Prefix)).ToList())
            {
                RefToId.Remove(rf);
                _roleCounters.Remove(prefix);
            }
            Refs.RemoveAll(r => prefixes.Contains(r.Prefix));
        }
    }
}
