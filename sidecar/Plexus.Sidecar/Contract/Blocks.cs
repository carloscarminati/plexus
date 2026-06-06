using System.Text.Json;
using System.Text.Json.Serialization;

namespace Plexus.Sidecar.Contract;

// Mirror of contract/blocks.ts — keep the two in sync. Bump SchemaVersion on
// any breaking change. The discriminator property is "type"; values match the
// TypeScript string literals exactly.
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
    public string Text { get; set; } = "";
}

public sealed class TableBlock : Block
{
    public List<TableColumn> Columns { get; set; } = new();
    public List<Dictionary<string, JsonElement>> Rows { get; set; } = new();
    public string? Caption { get; set; }
}

public sealed class TableColumn
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Align { get; set; } // "left" | "right" | "center"
}

public sealed class LinkCardBlock : Block
{
    public string Url { get; set; } = "";
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Image { get; set; } // OG image; resolved by the sidecar.
}

public sealed class CodeBlock : Block
{
    public string Language { get; set; } = "";
    public string Code { get; set; } = "";
    public string? Filename { get; set; }
}

public sealed class ChartBlock : Block // P1
{
    public string Chart { get; set; } = "line"; // "line" | "bar" | "scatter"
    public List<string>? XLabels { get; set; }
    public List<ChartSeries> Series { get; set; } = new();
    public string? XTitle { get; set; }
    public string? YTitle { get; set; }
}

public sealed class ChartSeries
{
    public string? Name { get; set; }
    public List<double> Values { get; set; } = new();
}

public sealed class ChoicesBlock : Block // P1 — interactive
{
    public string? Prompt { get; set; }
    public List<ChoiceOption> Options { get; set; } = new();
}

public sealed class ChoiceOption
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
}

public sealed class McpUiBlock : Block // P2
{
    public string ResourceUri { get; set; } = "";
    public string MimeType { get; set; } = "";
}
