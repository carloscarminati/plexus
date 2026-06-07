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
    string PromptShape,     // model-facing shape + usage note (migrated verbatim from SystemPrompt)
    // Optional per-entry hooks — keep type-specific contract logic INSIDE the entry,
    // not scattered across the sidecar. RefineSchema tightens the generated branch
    // (enums, additionalProperties:false, conditional requireds); MigrateLegacy
    // upconverts an old persisted shape to the current one on load (null = no change);
    // ValidateSemantic runs AFTER schema validation for rules JSON Schema can't express
    // (returns an error string → invalid → fallback, or null when OK).
    Action<JsonObject>? RefineSchema = null,
    Func<JsonObject, JsonObject?>? MigrateLegacy = null,
    Func<JsonObject, string?>? ValidateSemantic = null);

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
              { "type": "chart", "mark": "bar|line|point|arc|area|rect",
                "data": [ { "<field>": <value>, ... } ],
                "encoding": {
                  "x": { "field": "<field>", "type": "quantitative|nominal|ordinal|temporal" },
                  "y": { "field": "<field>", "type": "..." },
                  "color": { "field": "<field>" },
                  "theta": { "field": "<field>" },
                  "size": { "field": "<field>" } },
                "title": "<optional>", "legend": <true|false>, "stack": <true|false> }
                // Inline data only (array of records). Choose the mark by intent:
                //   composition / part-to-whole → arc;  trend over time → line;
                //   category comparison → bar;  part-to-whole over time → area (stack:true);
                //   distribution / matrix → point / rect.
                // bar/line/area/rect need x+y; arc needs theta; point uses x+y (+size).
                // Set "type" on every axis: use "ordinal" for year / integer / sequential
                //   axes (discrete ticks like 2014…2024); "temporal" ONLY for real date
                //   strings (e.g. "2024-03"); "quantitative" for continuous measures.
                // Multi-series or stacked: use LONG-format records — one row per
                //   x × series, with the series name in its OWN field bound to "color"
                //   (NOT one column per series). Every encoding field must be a key in data.
                // A single-series line/area needs only x+y. Use "color" only to separate
                //   multiple series; never map the same measure to both y and color/size.
                // No data URLs, transforms, selections, or expressions.
            """,
            RefineSchema: RefineChartSchema,
            MigrateLegacy: UpconvertLegacyChart,
            ValidateSemantic: ValidateChartFields),

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
            e.RefineSchema?.Invoke(node); // entry-owned tightening (e.g. chart)
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
        if (!result.IsValid)
        {
            errors = result.Details
                .Where(d => d.HasErrors)
                .SelectMany(d => d.Errors!.Values.Select(v => $"{d.InstanceLocation}: {v}"))
                .DefaultIfEmpty("schema validation failed")
                .ToList();
            return false;
        }

        // Post-schema semantic checks the JSON Schema can't express (e.g. a chart
        // encoding field must exist in the data records). Catalog-owned per entry.
        var semantic = new List<string>();
        if (blocksArray is JsonArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                if (arr[i] is not JsonObject obj)
                    continue;
                var entry = Entries.FirstOrDefault(e => e.TypeName == (string?)obj["type"]);
                var err = entry?.ValidateSemantic?.Invoke(obj);
                if (err is not null)
                    semantic.Add($"[{i}]: {err}");
            }
        }
        errors = semantic;
        return semantic.Count == 0;
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

    // Upconvert any legacy block shapes in a persisted blocks-array JSON to the
    // current shapes, so old graphs load + render under the current renderers.
    // Generic: it dispatches to each entry's MigrateLegacy by discriminator — no
    // type-specific logic lives here or in the persistence layer.
    public static string MigrateLegacyJson(string blocksJson)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(blocksJson); }
        catch (JsonException) { return blocksJson; }
        if (root is not JsonArray arr)
            return blocksJson;

        var changed = false;
        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is not JsonObject obj)
                continue;
            var type = (string?)obj["type"];
            var entry = Entries.FirstOrDefault(e => e.TypeName == type);
            var migrated = entry?.MigrateLegacy?.Invoke(obj);
            if (migrated is not null)
            {
                arr[i] = migrated;
                changed = true;
            }
        }
        return changed ? arr.ToJsonString() : blocksJson;
    }

    // ── chart (C1) — type-specific contract logic, owned by the chart entry ──────

    // Tighten the generated chart branch: enumerate the curated marks, forbid any
    // field outside the whitelist (data URLs / transforms / selections / expressions),
    // and require the channels each mark needs.
    private static void RefineChartSchema(JsonObject branch)
    {
        var props = branch["properties"]!.AsObject();
        props["mark"] = new JsonObject
        {
            ["enum"] = new JsonArray("bar", "line", "point", "arc", "area", "rect"),
        };
        branch["additionalProperties"] = false; // whitelist by construction
        if (props["encoding"] is JsonObject enc)
            enc["additionalProperties"] = false; // only the curated channels

        branch["allOf"] = new JsonArray
        {
            MarkRequires(new[] { "bar", "line", "area", "rect", "point" }, new[] { "x", "y" }),
            MarkRequires(new[] { "arc" }, new[] { "theta" }),
        };
    }

    private static JsonObject MarkRequires(string[] marks, string[] channels) => new()
    {
        ["if"] = new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["mark"] = new JsonObject { ["enum"] = new JsonArray(marks.Select(m => (JsonNode)m).ToArray()) },
            },
            ["required"] = new JsonArray("mark"),
        },
        ["then"] = new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["encoding"] = new JsonObject { ["required"] = new JsonArray(channels.Select(c => (JsonNode)c).ToArray()) },
            },
        },
    };

    // Every encoding field must be a key in the data records. Catches wide-format /
    // misnamed multi-series specs (e.g. color→"series" when records have no such key)
    // that pass the schema but render broken → turned into an honest fallback.
    private static string? ValidateChartFields(JsonObject chart)
    {
        if (chart["data"] is not JsonArray data || data.Count == 0)
            return null; // nothing to check against
        var keys = new HashSet<string>();
        foreach (var rec in data)
            if (rec is JsonObject o)
                foreach (var kv in o)
                    keys.Add(kv.Key);
        if (keys.Count == 0)
            return null;

        if (chart["encoding"] is not JsonObject enc)
            return null;
        foreach (var ch in enc)
        {
            var field = (ch.Value as JsonObject)?["field"];
            var name = (string?)field;
            if (name is not null && !keys.Contains(name))
                return $"encoding.{ch.Key}.field '{name}' is not a key in the chart data";
        }
        return null;
    }

    // Old shape {chart: line|bar|scatter, xLabels, series:[{name,values}]} → curated
    // {mark, data:[{x,y,series}], encoding{x,y,color}}. Returns null if already current.
    private static JsonObject? UpconvertLegacyChart(JsonObject old)
    {
        if (old["mark"] is not null || old["series"] is not JsonArray series)
            return null; // already the current shape (or not a legacy chart)

        var mark = (string?)old["chart"] switch
        {
            "scatter" => "point",
            "bar" => "bar",
            _ => "line",
        };

        var xLabels = old["xLabels"] as JsonArray;
        var hasName = series.Count > 0 && series[0] is JsonObject s0 && s0["name"] is not null;
        var multi = series.Count > 1;

        var data = new JsonArray();
        for (var si = 0; si < series.Count; si++)
        {
            if (series[si] is not JsonObject s)
                continue;
            var name = (string?)s["name"] ?? $"Series {si + 1}";
            var values = s["values"] as JsonArray ?? new JsonArray();
            for (var i = 0; i < values.Count; i++)
            {
                var label = xLabels is not null && i < xLabels.Count
                    ? xLabels[i]!.DeepClone()
                    : JsonValue.Create(i + 1);
                data.Add(new JsonObject
                {
                    ["x"] = label,
                    ["y"] = values[i]?.DeepClone() ?? JsonValue.Create(0),
                    ["series"] = name,
                });
            }
        }

        var encoding = new JsonObject
        {
            ["x"] = new JsonObject { ["field"] = "x", ["type"] = "nominal" },
            ["y"] = new JsonObject { ["field"] = "y", ["type"] = "quantitative" },
        };
        if (multi || hasName)
            encoding["color"] = new JsonObject { ["field"] = "series", ["type"] = "nominal" };

        var result = new JsonObject
        {
            ["type"] = "chart",
            ["mark"] = mark,
            ["data"] = data,
            ["encoding"] = encoding,
        };
        if (old["yTitle"] is JsonNode yt && (string?)yt is { Length: > 0 } title)
            result["title"] = title;
        return result;
    }
}
