using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using Json.Schema;
using Plexus.Sidecar.Services;

namespace Plexus.Sidecar.Contract;

// One entry per block type. The catalog is the SINGLE SOURCE OF TRUTH for the
// block contract: the model prompt section and the JSON Schema are generated from
// it, and emitted blocks are validated against that schema. Adding a block type =
// one new entry (+ its payload class). No hardcoded type list elsewhere generates
// the prompt or schema.
public sealed record BlockTypeEntry(
    string TypeName,        // discriminator value (matches blocks.ts + [JsonDerivedType])
    Type PayloadType,       // C# payload class — drives schema generation
    bool ModelEmitted,      // appears in the model prompt (mcp_ui is host-emitted, not prompted)
    string PromptShape);    // model-facing shape + usage note (migrated verbatim from SystemPrompt)

public static class BlockCatalog
{
    public static readonly IReadOnlyList<BlockTypeEntry> Entries = new[]
    {
        new BlockTypeEntry("markdown", typeof(MarkdownBlock), true,
            """  { "type": "markdown", "text": "<GFM markdown>" }"""),

        new BlockTypeEntry("table", typeof(TableBlock), true,
            """
              { "type": "table",
                "columns": [ { "key": "<id>", "label": "<header>", "align": "left|right|center" } ],
                "rows": [ { "<key>": <string|number|boolean|null>, ... } ],
                "caption": "<optional>" }
            """),

        new BlockTypeEntry("link_card", typeof(LinkCardBlock), true,
            """
              { "type": "link_card", "url": "<https url>",
                "title": "<optional>", "description": "<optional>" }
                // Do NOT invent an "image"; the app resolves the site preview itself.
            """),

        new BlockTypeEntry("code", typeof(CodeBlock), true,
            """  { "type": "code", "language": "<lang>", "code": "<source>", "filename": "<optional>" }"""),

        new BlockTypeEntry("chart", typeof(ChartBlock), true,
            """
              { "type": "chart", "chart": "line|bar|scatter",
                "series": [ { "name": "<optional>", "values": [<number>, ...] } ],
                "xLabels": ["<optional>", ...], "xTitle": "<optional>", "yTitle": "<optional>" }
                // Use for numeric series worth visualizing. All series share xLabels.
            """),

        new BlockTypeEntry("choices", typeof(ChoicesBlock), true,
            """
              { "type": "choices", "prompt": "<optional>",
                "options": [ { "id": "<short-id>", "label": "<button text>" } ] }
                // Offer a SMALL set of next actions. When the user clicks one, the app
                // sends its label back as their next message — so write options as the
                // thing the user would say next. Only use when genuinely offering a choice.
            """),

        // mcp_ui is emitted by the MCP host (M1), never by the model — so it is part
        // of the contract/schema but NOT described in the prompt.
        new BlockTypeEntry("mcp_ui", typeof(McpUiBlock), false, ""),
    };

    // The block-shapes section of the model prompt — only the model-emitted entries,
    // in catalog order, separated by a blank line (matches the prior hand-written layout).
    public static string PromptSection { get; } =
        string.Join("\n\n", Entries.Where(e => e.ModelEmitted).Select(e => e.PromptShape.TrimEnd()));

    // JSON Schema for a blocks array, generated from the catalog payload types via
    // JsonSchemaExporter (.NET 9). Each branch is pinned to its discriminator.
    public static JsonNode SchemaNode { get; } = BuildSchemaNode();

    private static readonly JsonSchema _schema = JsonSchema.FromText(SchemaNode.ToJsonString());

    // Discriminator names actually present in the generated schema (for tests).
    public static IReadOnlyList<string> SchemaTypeNames { get; } =
        SchemaNode["items"]!["anyOf"]!.AsArray()
            .Select(b => (string)b!["properties"]!["type"]!["const"]!)
            .ToList();

    private static JsonNode BuildSchemaNode()
    {
        // Exporter needs an explicit resolver; clone our conventions (camelCase, etc.).
        var exportOpts = new JsonSerializerOptions(PlexusJson.Options) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

        var branches = new JsonArray();
        foreach (var e in Entries)
        {
            var node = exportOpts.GetJsonSchemaAsNode(e.PayloadType).AsObject();

            var props = node["properties"]?.AsObject() ?? new JsonObject();
            props["type"] = new JsonObject { ["const"] = e.TypeName }; // pin the discriminator
            node["properties"] = props;

            var required = node["required"]?.AsArray() ?? new JsonArray();
            if (!required.Any(n => (string?)n == "type"))
                required.Add("type");
            node["required"] = required;

            node["type"] = "object"; // an item must be an object (drop the exporter's "null")
            branches.Add(node);
        }

        return new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject { ["anyOf"] = branches },
        };
    }

    // Validate a blocks array (the value of `blocks`) against the generated schema.
    public static bool ValidateBlocksArray(JsonNode? blocksArray, out IReadOnlyList<string> errors)
    {
        var result = _schema.Evaluate(blocksArray, new EvaluationOptions { OutputFormat = OutputFormat.List });
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

    // Strategy (a), centralized: validate the emitted envelope's blocks against the
    // catalog schema, then deserialize. Returns false (→ heuristic fallback) on any
    // structural problem — unknown type, wrong field type, or a missing required field.
    public static bool TryParse(string json, out List<Block> blocks)
    {
        blocks = new List<Block>();
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return false; }

        var blocksNode = root?["blocks"];
        if (blocksNode is not JsonArray)
            return false;

        if (!ValidateBlocksArray(blocksNode, out _))
            return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<List<Block>>(blocksNode.ToJsonString(), PlexusJson.Options);
            if (parsed is { Count: > 0 })
            {
                blocks = parsed;
                return true;
            }
        }
        catch (JsonException)
        {
            // schema passed but deserialization disagreed — fall back, don't throw.
        }
        return false;
    }
}
