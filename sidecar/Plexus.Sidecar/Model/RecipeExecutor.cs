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
public sealed record RecipeRunResult(bool Ok, Graph Graph, string? Error = null, string? FailedStepId = null);

public static class RecipeExecutor
{
    public static async Task<RecipeRunResult> RunAsync(
        IChatClient client,
        Recipe recipe,
        string modelId,
        Func<RecipeStep, Graph, CancellationToken, Task>? onDecisionSeam = null,
        int maxAttempts = 3,
        CancellationToken ct = default)
    {
        var state = new RunState();

        foreach (var step in recipe.Steps)
        {
            var schema = SchemaForStep(step);
            var result = await SchemaConstrainedEmitter.EmitAsync(
                client, modelId, step.Prompt, schema, maxAttempts,
                postStructuralCheck: RefCheckFor(step, state), ct: ct);

            // A structured emission that never validates must surface explicitly — it
            // can NOT degrade to free text without breaking the typed-graph contract.
            if (!result.Ok || result.Value is null)
                return new RecipeRunResult(false, state.Graph, result.Error ?? "emission failed", step.Id);

            ApplyStep(step, result.Value, state);

            if (step.DecisionSeam && onDecisionSeam is not null)
                await onDecisionSeam(step, state.Graph, ct);
        }

        return new RecipeRunResult(true, state.Graph);
    }

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
    private static Func<JsonNode, IReadOnlyList<string>>? RefCheckFor(RecipeStep step, RunState state) =>
        step.Role is ReasoningRoles.Hypothesis or ReasoningRoles.Evaluation or ReasoningRoles.Conclusion
            ? json => UnresolvedRefs(step.Role, json, state.RefToId)
            : null;

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
                s.Bind(prefix, s.AddNode(ReasoningRoles.Frame, text, new ReasoningMeta { Role = ReasoningRoles.Frame }));
                break;
            }
            case ReasoningRoles.Fact:
            {
                foreach (var f in value["facts"]!.Deserialize<List<FactEmission>>(PlexusJson.Options)!)
                    s.Bind(prefix, s.AddNode(ReasoningRoles.Fact, f.Claim,
                        new ReasoningMeta { Role = ReasoningRoles.Fact, SourceKind = f.SourceKind, SourceRef = f.SourceRef }));
                break;
            }
            case ReasoningRoles.Uncertainty:
            {
                foreach (var u in value["uncertainties"]!.Deserialize<List<UncertaintyEmission>>(PlexusJson.Options)!)
                    s.Bind(prefix, s.AddNode(ReasoningRoles.Uncertainty, u.Question, new ReasoningMeta { Role = ReasoningRoles.Uncertainty }));
                break;
            }
            case ReasoningRoles.Hypothesis:
            {
                foreach (var h in value["hypotheses"]!.Deserialize<List<HypothesisEmission>>(PlexusJson.Options)!)
                {
                    var hid = s.AddNode(ReasoningRoles.Hypothesis, h.Statement, new ReasoningMeta { Role = ReasoningRoles.Hypothesis });
                    s.Bind(prefix, hid);
                    foreach (var uref in h.Addresses ?? Enumerable.Empty<string>())
                        if (s.RefToId.TryGetValue(uref, out var uid))
                            s.Graph.Edges.Add(new Edge { From = hid, To = uid, Kind = ReasoningEdges.Addresses });
                }
                break;
            }
            case ReasoningRoles.Evaluation:
            {
                var ev = value.Deserialize<EvaluationEmission>(PlexusJson.Options)!;
                s.Bind(prefix, s.AddNode(ReasoningRoles.Evaluation, "Evaluation", new ReasoningMeta { Role = ReasoningRoles.Evaluation }));
                foreach (var w in ev.Weighings)
                    if (s.RefToId.TryGetValue(w.Fact, out var fid) && s.RefToId.TryGetValue(w.Hypothesis, out var hid))
                        s.Graph.Edges.Add(new Edge { From = fid, To = hid, Kind = EdgeKindForStance(w.Stance), Weight = w.Weight });
                break;
            }
            case ReasoningRoles.Conclusion:
            {
                var c = value.Deserialize<ConclusionEmission>(PlexusJson.Options)!;
                var cid = s.AddNode(ReasoningRoles.Conclusion, c.Summary ?? "Conclusion", new ReasoningMeta { Role = ReasoningRoles.Conclusion });
                s.Bind(prefix, cid);
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
    // steps resolve against, and per-role ref counters (f0, h1, …).
    private sealed class RunState
    {
        public Graph Graph { get; } = new() { Id = "recipe" };
        public Dictionary<string, string> RefToId { get; } = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _roleCounters = new(StringComparer.Ordinal);
        private int _seq;
        private string? _frameId;

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

        // Assign the next per-role ref (f0, f1, …) to a freshly created node id.
        public void Bind(string prefix, string nodeId)
        {
            var n = _roleCounters.TryGetValue(prefix, out var c) ? c : 0;
            _roleCounters[prefix] = n + 1;
            RefToId[$"{prefix}{n}"] = nodeId;
        }
    }
}
