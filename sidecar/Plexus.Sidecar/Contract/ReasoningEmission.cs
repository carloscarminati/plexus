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

// Compiled schemas for the reasoning emission payloads. Built once via JsonSchemaGen
// (same generation + warmup the block catalog uses); the recipe engine (R2.0b) points
// the emitter at these.
public static class ReasoningSchemas
{
    public static JsonNode FactNode { get; } = JsonSchemaGen.ForType(typeof(FactEmission));

    public static JsonSchema Fact { get; } = JsonSchemaGen.Compile(
        FactNode,
        warmupSample: new JsonObject { ["claim"] = "w", ["sourceKind"] = "doc", ["sourceRef"] = "w" });
}
