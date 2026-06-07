using System.Text.Json;
using System.Text.Json.Serialization;

namespace Plexus.Sidecar.Contract;

// Mirror of contract/blocks.ts — keep the two in sync. Bump SchemaVersion on
// any breaking change. The discriminator property is "type"; values match the
// TypeScript string literals exactly.
//
// The model prompt and the JSON Schema for emitted blocks are generated from
// `BlockCatalog` (the single source of truth). `[JsonRequired]` marks the fields
// the contract requires (non-optional in blocks.ts): it drives the schema's
// `required` and makes deserialization reject a block missing them — the same
// notion of "required" on both sides.
public static class BlockSchema
{
    public const int Version = 1;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(MarkdownBlock), "markdown")]
[JsonDerivedType(typeof(TableBlock), "table")]
[JsonDerivedType(typeof(LinkCardBlock), "link_card")]
[JsonDerivedType(typeof(CodeBlock), "code")]
[JsonDerivedType(typeof(ChartBlock), "chart")]
[JsonDerivedType(typeof(ChoicesBlock), "choices")]
[JsonDerivedType(typeof(McpUiBlock), "mcp_ui")]
public abstract class Block { }

public sealed class MarkdownBlock : Block
{
    [JsonRequired] public string Text { get; set; } = "";
}

public sealed class TableBlock : Block
{
    [JsonRequired] public List<TableColumn> Columns { get; set; } = new();
    [JsonRequired] public List<Dictionary<string, JsonElement>> Rows { get; set; } = new();
    public string? Caption { get; set; }
}

public sealed class TableColumn
{
    [JsonRequired] public string Key { get; set; } = "";
    [JsonRequired] public string Label { get; set; } = "";
    public string? Align { get; set; } // "left" | "right" | "center"
}

public sealed class LinkCardBlock : Block
{
    [JsonRequired] public string Url { get; set; } = "";
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Image { get; set; } // OG image; resolved by the sidecar.
}

public sealed class CodeBlock : Block
{
    [JsonRequired] public string Language { get; set; } = "";
    [JsonRequired] public string Code { get; set; } = "";
    public string? Filename { get; set; }
}

// C1 — a curated Vega-Lite subset (channel-based, generalizes across marks). NOT a
// full Vega-Lite passthrough: data URLs, transforms, selections and expressions are
// forbidden by construction (no fields for them) and rejected by the schema
// (additionalProperties:false, applied via the catalog entry's refinement).
public sealed class ChartBlock : Block // P1 → C1
{
    [JsonRequired] public string Mark { get; set; } = "bar"; // bar|line|point|arc|area|rect
    [JsonRequired] public List<Dictionary<string, JsonElement>> Data { get; set; } = new(); // inline records
    [JsonRequired] public ChartEncoding Encoding { get; set; } = new();
    public string? Title { get; set; }
    public bool? Legend { get; set; }
    public bool? Stack { get; set; } // bar/area
}

// channel → field mapping appropriate to the mark.
public sealed class ChartEncoding
{
    public ChartChannel? X { get; set; }
    public ChartChannel? Y { get; set; }
    public ChartChannel? Color { get; set; }
    public ChartChannel? Theta { get; set; } // magnitude — for arc (pie/donut)
    public ChartChannel? Size { get; set; }  // for point
}

public sealed class ChartChannel
{
    [JsonRequired] public string Field { get; set; } = "";
    public string? Type { get; set; } // quantitative|nominal|ordinal|temporal
}

public sealed class ChoicesBlock : Block // P1 — interactive
{
    public string? Prompt { get; set; }
    [JsonRequired] public List<ChoiceOption> Options { get; set; } = new();
}

public sealed class ChoiceOption
{
    [JsonRequired] public string Id { get; set; } = "";
    [JsonRequired] public string Label { get; set; } = "";
}

public sealed class McpUiBlock : Block // P2
{
    [JsonRequired] public string ResourceUri { get; set; } = "";
    [JsonRequired] public string MimeType { get; set; } = "";
}
