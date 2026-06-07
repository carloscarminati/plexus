using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Model;

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
        new object[] { "chart", """{"type":"chart","chart":"line","series":[{"name":"s","values":[1,2,3]}],"xLabels":["a","b","c"]}""" },
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

        var inPrompt = Regex.Matches(SystemPrompt.Text, """"type"\s*:\s*"([a-z_]+)"""")
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

    // Live path parity: a valid envelope parses via strategy (a); junk falls back to
    // the heuristic parser (never throws, always renders).
    [Fact]
    public void ParseBlocks_uses_strategy_a_for_valid_and_falls_back_for_junk()
    {
        var good = AnthropicTurnService.ParseBlocks("""{"blocks":[{"type":"code","language":"py","code":"print(1)"}]}""");
        Assert.Single(good);
        Assert.IsType<CodeBlock>(good[0]);

        var junk = AnthropicTurnService.ParseBlocks("just some prose, not JSON at all");
        Assert.Single(junk);
        Assert.IsType<MarkdownBlock>(junk[0]); // fallback parser

        // A malformed strategy-(a) envelope (missing required field) → fallback, not a throw.
        var malformed = AnthropicTurnService.ParseBlocks("""{"blocks":[{"type":"code","language":"py"}]}""");
        Assert.NotEmpty(malformed);
        Assert.IsType<MarkdownBlock>(malformed[0]);
    }
}
