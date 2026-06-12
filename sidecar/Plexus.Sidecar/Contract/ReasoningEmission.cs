using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Json.Schema;

namespace Plexus.Sidecar.Contract;

// ADR-0002 R2.0a — emission payloads for reasoning primitives. These are the typed
// fragments a recipe step asks the model to emit (e.g. step 2 "extract facts"). The
// schema is generated via the SAME JsonSchemaGen helper the render catalog uses, so
// the auto-fix emitter validates reasoning output exactly as the render path validates
// blocks. `[JsonRequired]` marks STRUCTURAL requirements (the layer R2.0a enforces);
// SEMANTIC requirements (source_ref must be non-empty AND resolve) are R1 invariants,
// checked over the assembled graph in R2.0b.
public sealed class FactEmission
{
    [JsonRequired] public string Claim { get; set; } = "";      // the atomic fact text
    [JsonRequired] public string SourceKind { get; set; } = ""; // one of FactSources (doc|api|given)
    [JsonRequired] public string SourceRef { get; set; } = "";  // provenance reference (URI/string)
}

public sealed class HypothesisEmission
{
    [JsonRequired] public string Statement { get; set; } = ""; // a candidate explanation
    public List<string>? Addresses { get; set; }               // uncertainty refs this hypothesis would resolve
}

public sealed class UncertaintyEmission
{
    [JsonRequired] public string Question { get; set; } = ""; // a gap / unknown to resolve
}

public sealed class FrameEmission
{
    [JsonRequired] public string Question { get; set; } = ""; // the case question
    public string? Scope { get; set; }                        // scope / constraints
}

// One weighing in the evaluation step: a fact for/against a hypothesis, by magnitude.
// Weight is a 0..1 MAGNITUDE — the sign comes from Stance (supports/refutes). It maps
// directly to Edge.Weight (the non-derivable datum R2.1 must persist), so the
// evaluation round-trips without a DTO redo when relational-edge persistence lands.
public sealed class WeighingEmission
{
    [JsonRequired] public string Fact { get; set; } = "";       // a fact ref
    [JsonRequired] public string Hypothesis { get; set; } = ""; // a hypothesis ref
    [JsonRequired] public string Stance { get; set; } = "";     // "supports" | "refutes"
    [JsonRequired] public double Weight { get; set; }           // magnitude → Edge.Weight
}

public sealed class EvaluationEmission
{
    [JsonRequired] public List<WeighingEmission> Weighings { get; set; } = new();
    // F2 — the qualitative "why" behind the weighing (overall, one per evaluation): which
    // evidence tipped it, why the selected hypothesis beats the rivals. Optional (additive,
    // backward-compatible); the quantitative verdict still derives from the edge weights.
    public string? Rationale { get; set; }
}

public sealed class ConclusionEmission
{
    [JsonRequired] public string Selects { get; set; } = "";  // the chosen hypothesis ref
    [JsonRequired] public List<string> Cites { get; set; } = new(); // fact refs the chain renders
    public string? Summary { get; set; }
}

// Compiled schemas for the reasoning emission payloads. Built once via JsonSchemaGen
// (same generation + warmup the block catalog uses); the recipe engine (R2.0b) points
// the emitter at these per step.
//
// Cardinality is per-step (ADR-0002 R2.0b): single-node steps (frame, evaluation,
// conclusion) emit a singular object; multi-node steps (facts, uncertainties,
// hypotheses) emit a BOUNDED array in one call — the set must be reasoned as a set
// (a non-duplicative fact set, a space-covering hypothesis fan-out), which only
// holds when the model sees the items together. minItems/maxItems are a cheap
// structural quality guard (a 1-hypothesis "fan-out" kills the step-5 contrast; a
// runaway fan-out is noise) caught before the R1 semantic invariants.
public static class ReasoningSchemas
{
    // Singular emission (single-node steps).
    public static JsonSchema Fact { get; } = JsonSchemaGen.Compile(
        RefinedItemSchema(typeof(FactEmission)),
        warmupSample: new JsonObject { ["claim"] = "w", ["sourceKind"] = "doc", ["sourceRef"] = "w" });

    // Bounded-array emission (multi-node steps): a `{ "<key>": [ … ] }` envelope.
    public static JsonSchema Facts { get; } = BoundedArrayEnvelope("facts", typeof(FactEmission), minItems: 1);
    public static JsonSchema Hypotheses { get; } = BoundedArrayEnvelope("hypotheses", typeof(HypothesisEmission), minItems: 2, maxItems: 6);
    public static JsonSchema Uncertainties { get; } = BoundedArrayEnvelope("uncertainties", typeof(UncertaintyEmission), minItems: 1);

    // Singular emission (single-node steps).
    public static JsonSchema Frame { get; } = JsonSchemaGen.Compile(
        JsonSchemaGen.ForType(typeof(FrameEmission)), warmupSample: new JsonObject { ["question"] = "w" });

    public static JsonSchema Conclusion { get; } = JsonSchemaGen.Compile(
        JsonSchemaGen.ForType(typeof(ConclusionEmission)),
        warmupSample: new JsonObject { ["selects"] = "w", ["cites"] = new JsonArray("w") });

    // Evaluation bounds each weighing's weight to [0,1]: a runaway weight (1.5, -0.3)
    // would corrupt the net-evidence sum the R1 contrast invariant computes, so it's a
    // cheap STRUCTURAL guard (unlike the array bounds, the [0,1] range is intrinsic to a
    // magnitude, not a per-recipe knob, so it lives in the schema, not the step config).
    public static JsonSchema Evaluation { get; } = BuildEvaluationSchema();

    private static JsonSchema BuildEvaluationSchema()
    {
        var node = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["weighings"] = new JsonObject { ["type"] = "array", ["items"] = RefinedItemSchema(typeof(WeighingEmission)) },
                // F2 — the comparative rationale, emitted alongside the weighings in one call.
                // Not required (additive): a model that omits it still validates structurally.
                ["rationale"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray("weighings"),
        };
        return JsonSchemaGen.Compile(node, warmupSample: new JsonObject { ["weighings"] = new JsonArray(new JsonObject()) });
    }

    // Per-primitive STRUCTURAL refinements the exporter can't express from the C# type:
    // closed vocabularies (a fact's source_kind, a weighing's stance) and the weight's
    // [0,1] magnitude. These force a real model into the contract — an out-of-vocab value
    // (e.g. stance "neutral") fails structurally and is re-prompted, instead of crashing
    // assembly or being silently dropped.
    internal static JsonObject RefinedItemSchema(Type itemType)
    {
        var node = JsonSchemaGen.ForType(itemType);
        if (itemType == typeof(FactEmission))
        {
            SetEnum(node, "sourceKind", FactSources.Doc, FactSources.Api, FactSources.Given);
        }
        else if (itemType == typeof(WeighingEmission))
        {
            SetEnum(node, "stance", ReasoningEdges.Supports, ReasoningEdges.Refutes);
            var weight = node["properties"]?["weight"]?.AsObject();
            if (weight is not null) { weight["minimum"] = 0; weight["maximum"] = 1; }
        }
        return node;
    }

    private static void SetEnum(JsonObject node, string property, params string[] values)
    {
        var prop = node["properties"]?[property]?.AsObject();
        if (prop is not null)
            prop["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray());
    }

    // Build an object envelope whose single required property is a bounded array of
    // `itemType`. The recipe step config supplies (key, itemType, bounds); the emitter
    // validates/auto-fixes the whole collection against it.
    internal static JsonSchema BoundedArrayEnvelope(string key, Type itemType, int minItems, int? maxItems = null)
    {
        var array = new JsonObject
        {
            ["type"] = "array",
            ["items"] = RefinedItemSchema(itemType),
            ["minItems"] = minItems,
        };
        if (maxItems is int max)
            array["maxItems"] = max;

        var node = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { [key] = array },
            ["required"] = new JsonArray(key),
        };

        // Warmup must descend into `items` to build the per-item constraints, so the
        // sample carries minItems (≥1) entries; their validity is irrelevant (the
        // result is discarded — we only force JsonSchema.Net's lazy build).
        var warmItems = new JsonArray(Enumerable.Range(0, Math.Max(1, minItems))
            .Select(_ => (JsonNode)new JsonObject()).ToArray());
        return JsonSchemaGen.Compile(node, warmupSample: new JsonObject { [key] = warmItems });
    }
}
