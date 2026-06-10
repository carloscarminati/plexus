using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using Json.Schema;

namespace Plexus.Sidecar.Contract;

// Generic schema-generation + validation primitives, factored out of BlockCatalog
// (ADR-0002 R2.0a) so the reasoning layer can reuse the SAME mechanism the render
// catalog uses — JsonSchemaExporter (.NET 9) for generation, JsonSchema.Net for
// validation — instead of a parallel stack. BlockCatalog now delegates here; the
// reasoning emission schemas (R2.0b) target the same helper. No render behavior
// changes (the block schema/validation is byte-for-byte the same).
public static class JsonSchemaGen
{
    // Clone the sidecar's JSON conventions (camelCase, etc.); the exporter needs an
    // explicit resolver. Shared across all generated schemas.
    private static readonly JsonSerializerOptions ExportOptions =
        new(Services.PlexusJson.Options) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    // JSON Schema (as a mutable node) for a payload type. Callers may further pin
    // discriminators / tighten constraints before compiling.
    public static JsonObject ForType(Type payloadType) =>
        ExportOptions.GetJsonSchemaAsNode(payloadType).AsObject();

    // Compile a schema node for evaluation. JsonSchema.Net builds its internal
    // evaluation state lazily on the FIRST Evaluate and that build is not thread-safe;
    // a representative warmup sample forces it once (callers run this during static
    // init, serialized by the CLR) so later concurrent evaluations are read-only.
    public static JsonSchema Compile(JsonNode schemaNode, JsonNode? warmupSample = null)
    {
        var schema = JsonSchema.FromText(schemaNode.ToJsonString());
        if (warmupSample is not null)
            schema.Evaluate(warmupSample, new EvaluationOptions { OutputFormat = OutputFormat.List });
        return schema;
    }

    // Validate a value against a compiled schema. On failure, returns one error per
    // offending location ("{instanceLocation}: {message}") — the shape the auto-fix
    // loop feeds back to the model, and the render catalog already surfaced.
    public static bool Validate(JsonSchema schema, JsonNode? value, out IReadOnlyList<string> errors)
    {
        var result = schema.Evaluate(value, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid)
        {
            errors = Array.Empty<string>();
            return true;
        }

        errors = result.Details
            .Where(d => d.HasErrors)
            .SelectMany(d => d.Errors!.Values.Select(v => $"{d.InstanceLocation}: {v}"))
            .DefaultIfEmpty("schema validation failed")
            .ToList();
        return false;
    }
}
