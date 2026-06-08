using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Model;
using Plexus.Sidecar.Services;

namespace Plexus.Sidecar.Tests;

// C0 — the block contract is a single catalog; the model prompt and JSON Schema
// are generated from it, and emitted blocks are validated against that schema.
public class BlockCatalogTests
{
    // One known-GOOD sample per block type (wrapped as a 1-element blocks array).
    public static IEnumerable<object[]> GoodSamples() => new[]
    {
        new object[] { "markdown", """{"type":"markdown","text":"hello **world**"}""" },
        new object[] { "table", """{"type":"table","columns":[{"key":"a","label":"A"}],"rows":[{"a":"x"}],"caption":"c"}""" },
        new object[] { "link_card", """{"type":"link_card","url":"https://example.com","title":"t"}""" },
        new object[] { "code", """{"type":"code","language":"js","code":"const x = 1;"}""" },
        new object[] { "chart-bar", """{"type":"chart","mark":"bar","data":[{"k":"a","v":1},{"k":"b","v":2}],"encoding":{"x":{"field":"k","type":"nominal"},"y":{"field":"v","type":"quantitative"}},"title":"t"}""" },
        new object[] { "chart-arc", """{"type":"chart","mark":"arc","data":[{"c":"a","n":3},{"c":"b","n":5}],"encoding":{"theta":{"field":"n","type":"quantitative"},"color":{"field":"c","type":"nominal"}}}""" },
        new object[] { "chart-line", """{"type":"chart","mark":"line","data":[{"year":2014,"price":3.1},{"year":2015,"price":2.5}],"encoding":{"x":{"field":"year","type":"ordinal"},"y":{"field":"price","type":"quantitative"}}}""" },
        new object[] { "chart-point", """{"type":"chart","mark":"point","data":[{"h":2,"g":45},{"h":5,"g":70}],"encoding":{"x":{"field":"h","type":"quantitative"},"y":{"field":"g","type":"quantitative"}}}""" },
        new object[] { "chart-rect", """{"type":"chart","mark":"rect","data":[{"d":"Mon","h":"09","c":18}],"encoding":{"x":{"field":"h","type":"ordinal"},"y":{"field":"d","type":"ordinal"},"color":{"field":"c","type":"quantitative"}}}""" },
        new object[] { "chart-area-multi", """{"type":"chart","mark":"area","stack":true,"data":[{"year":2019,"source":"hydro","val":35},{"year":2019,"source":"solar","val":5},{"year":2020,"source":"hydro","val":36},{"year":2020,"source":"solar","val":8}],"encoding":{"x":{"field":"year","type":"ordinal"},"y":{"field":"val","type":"quantitative"},"color":{"field":"source","type":"nominal"}}}""" },
        new object[] { "choices", """{"type":"choices","prompt":"pick","options":[{"id":"a","label":"A"}]}""" },
        new object[] { "mcp_ui", """{"type":"mcp_ui","resourceUri":"ui://x","mimeType":"text/html"}""" },
    };

    [Theory]
    [MemberData(nameof(GoodSamples))]
    public void Schema_accepts_every_known_good_block(string type, string blockJson)
    {
        var arr = JsonNode.Parse($"[{blockJson}]");
        var ok = BlockCatalog.ValidateBlocksArray(arr, out var errors);
        Assert.True(ok, $"'{type}' should validate but didn't: {string.Join("; ", errors)}");
    }

    // Known-BAD samples — each must be rejected, not silently accepted.
    public static IEnumerable<object[]> BadSamples() => new[]
    {
        new object[] { "unknown type", """{"type":"banana","text":"x"}""" },
        new object[] { "wrong field type", """{"type":"markdown","text":123}""" },
        new object[] { "missing required (text)", """{"type":"markdown"}""" },
        new object[] { "missing required (code.code)", """{"type":"code","language":"js"}""" },
        new object[] { "missing required (table.rows)", """{"type":"table","columns":[{"key":"a","label":"A"}]}""" },
        new object[] { "missing discriminator", """{"text":"x"}""" },
        // chart (C1) curated-subset controls:
        new object[] { "chart unknown mark", """{"type":"chart","mark":"bubble","data":[{"x":1,"y":2}],"encoding":{"x":{"field":"x"},"y":{"field":"y"}}}""" },
        new object[] { "chart missing required encoding", """{"type":"chart","mark":"bar","data":[{"x":1}],"encoding":{}}""" },
        new object[] { "chart forbidden transform", """{"type":"chart","mark":"bar","data":[{"x":1,"y":2}],"encoding":{"x":{"field":"x"},"y":{"field":"y"}},"transform":[{"calculate":"1"}]}""" },
        new object[] { "chart forbidden data url", """{"type":"chart","mark":"bar","data":{"url":"https://evil/x.json"},"encoding":{"x":{"field":"x"},"y":{"field":"y"}}}""" },
        // (B) wide-format multi-series: color references a field absent from records.
        new object[] { "chart encoding field not in data", """{"type":"chart","mark":"area","stack":true,"data":[{"year":2019,"hydro":35,"solar":5}],"encoding":{"x":{"field":"year","type":"ordinal"},"y":{"field":"hydro","type":"quantitative"},"color":{"field":"source"}}}""" },
    };

    [Theory]
    [MemberData(nameof(BadSamples))]
    public void Schema_rejects_known_bad_block(string label, string blockJson)
    {
        var arr = JsonNode.Parse($"[{blockJson}]");
        var ok = BlockCatalog.ValidateBlocksArray(arr, out _);
        Assert.False(ok, $"'{label}' should be rejected but validated");
    }

    // Single source of truth — the SCHEMA reflects exactly the catalog entries.
    [Fact]
    public void Schema_types_are_exactly_the_catalog_entries()
    {
        var catalog = BlockCatalog.Entries.Select(e => e.TypeName).OrderBy(x => x).ToArray();
        var schema = BlockCatalog.SchemaTypeNames.OrderBy(x => x).ToArray();
        Assert.Equal(catalog, schema);
    }

    // Single source of truth — the PROMPT describes exactly the model-emitted entries,
    // none dropped or invented (guards against a drifting hand-written list).
    [Fact]
    public void Prompt_types_are_exactly_the_model_emitted_catalog_entries()
    {
        var expected = BlockCatalog.Entries.Where(e => e.ModelEmitted).Select(e => e.TypeName).OrderBy(x => x).ToArray();

        // Match the block discriminator only (a shape opens with `{ "type": "<name>"`),
        // not the chart channel's own "type" field (which lists encoding types).
        var inPrompt = Regex.Matches(SystemPrompt.Text, "\\{\\s*\"type\"\\s*:\\s*\"([a-z_]+)\"")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        Assert.Equal(expected, inPrompt);
        // mcp_ui is in the catalog/schema but is host-emitted → must NOT be prompted.
        Assert.DoesNotContain("mcp_ui", inPrompt);
    }

    // Drift guard — the [JsonDerivedType] registrations used for (de)serialization
    // must match the catalog exactly, so the two lists can't drift apart.
    [Fact]
    public void Polymorphic_registrations_match_the_catalog()
    {
        var registered = typeof(Block)
            .GetCustomAttributes<JsonDerivedTypeAttribute>()
            .Select(a => (Name: (string)a.TypeDiscriminator!, a.DerivedType))
            .OrderBy(x => x.Name)
            .ToArray();

        var catalog = BlockCatalog.Entries
            .Select(e => (Name: e.TypeName, DerivedType: e.PayloadType))
            .OrderBy(x => x.Name)
            .ToArray();

        Assert.Equal(catalog, registered);
    }

    // C1 — the chart entry's curated spec is reflected in the generated schema +
    // prompt automatically (no hardcoded chart list elsewhere).
    [Fact]
    public void Chart_schema_and_prompt_reflect_the_curated_spec()
    {
        var chartBranch = BlockCatalog.SchemaNode["items"]!["anyOf"]!.AsArray()
            .First(b => (string?)b!["properties"]!["type"]!["const"] == "chart")!.AsObject();

        var marks = chartBranch["properties"]!["mark"]!["enum"]!.AsArray().Select(n => (string)n!).ToArray();
        Assert.Equal(new[] { "bar", "line", "point", "arc", "area", "rect" }, marks);
        Assert.False(chartBranch["additionalProperties"]!.GetValue<bool>()); // forbids data url/transform/etc

        Assert.Contains("\"mark\"", SystemPrompt.Text);
        Assert.Contains("\"encoding\"", SystemPrompt.Text);
        Assert.Contains("arc", SystemPrompt.Text);          // mark guidance present
        Assert.DoesNotContain("xLabels", SystemPrompt.Text); // old shape gone
    }

    // C1 back-compat — an OLD-shape chart upconverts and loads under the new contract.
    [Fact]
    public void Legacy_chart_upconverts_to_curated_records()
    {
        var legacy = """[{"type":"chart","chart":"scatter","xLabels":["a","b"],"series":[{"name":"S1","values":[1,2]},{"name":"S2","values":[3,4]}]}]""";

        var migrated = BlockCatalog.MigrateLegacyJson(legacy);
        Assert.True(BlockCatalog.ValidateBlocksArray(JsonNode.Parse(migrated), out var errs), string.Join("; ", errs));

        var blocks = PlexusJson.Deserialize<List<Block>>(migrated)!;
        var chart = Assert.IsType<ChartBlock>(blocks[0]);
        Assert.Equal("point", chart.Mark);            // scatter → point
        Assert.Equal(4, chart.Data.Count);            // 2 series × 2 values → flat records
        Assert.Equal("x", chart.Encoding.X!.Field);
        Assert.Equal("y", chart.Encoding.Y!.Field);
        Assert.Equal("series", chart.Encoding.Color!.Field); // multi-series → color channel
    }

    // Live path parity: a valid envelope parses via strategy (a); junk falls back to
    // the heuristic parser (never throws, always renders).
    [Fact]
    public void ParseBlocks_uses_strategy_a_for_valid_and_falls_back_for_junk()
    {
        var good = BlockEmission.ParseBlocks("""{"blocks":[{"type":"code","language":"py","code":"print(1)"}]}""");
        Assert.Single(good);
        Assert.IsType<CodeBlock>(good[0]);

        var junk = BlockEmission.ParseBlocks("just some prose, not JSON at all");
        Assert.Single(junk);
        Assert.IsType<MarkdownBlock>(junk[0]); // fallback parser

        // A malformed strategy-(a) envelope (missing required field) → fallback, not a throw.
        var malformed = BlockEmission.ParseBlocks("""{"blocks":[{"type":"code","language":"py"}]}""");
        Assert.NotEmpty(malformed);
        Assert.IsType<MarkdownBlock>(malformed[0]);
    }
}
