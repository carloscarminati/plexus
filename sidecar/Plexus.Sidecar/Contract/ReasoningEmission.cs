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
}

public sealed class UncertaintyEmission
{
    [JsonRequired] public string Question { get; set; } = ""; // a gap / unknown to resolve
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
        JsonSchemaGen.ForType(typeof(FactEmission)),
        warmupSample: new JsonObject { ["claim"] = "w", ["sourceKind"] = "doc", ["sourceRef"] = "w" });

    // Bounded-array emission (multi-node steps): a `{ "<key>": [ … ] }` envelope.
    public static JsonSchema Facts { get; } = BoundedArrayEnvelope("facts", typeof(FactEmission), minItems: 1);
    public static JsonSchema Hypotheses { get; } = BoundedArrayEnvelope("hypotheses", typeof(HypothesisEmission), minItems: 2, maxItems: 6);
    public static JsonSchema Uncertainties { get; } = BoundedArrayEnvelope("uncertainties", typeof(UncertaintyEmission), minItems: 1);

    // Build an object envelope whose single required property is a bounded array of
    // `itemType`. The recipe step config supplies (key, itemType, bounds); the emitter
    // validates/auto-fixes the whole collection against it.
    internal static JsonSchema BoundedArrayEnvelope(string key, Type itemType, int minItems, int? maxItems = null)
    {
        var array = new JsonObject
        {
            ["type"] = "array",
            ["items"] = JsonSchemaGen.ForType(itemType),
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
