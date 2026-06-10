using System.Text.Json.Nodes;
using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Model;

namespace Plexus.Sidecar.Tests;

// ADR-0002 R2.0b (emission cardinality) — multi-node steps emit a BOUNDED array in
// one call so the model reasons about the set as a set. The bounds (minItems/maxItems)
// are a cheap structural quality guard caught before the R1 semantic invariants. The
// emitter is unchanged — same generic loop, pointed at a bounded-array envelope schema.
public class ReasoningEmissionCardinalityTests
{
    private const string OneHypothesis = """{"hypotheses":[{"statement":"h1"}]}""";          // < minItems 2
    private const string ThreeHypotheses = """{"hypotheses":[{"statement":"h1"},{"statement":"h2"},{"statement":"h3"}]}""";

    // Auto-fix over a collection: a degenerate fan-out (1 hypothesis) violates minItems
    // and is re-prompted; the corrected set of 3 validates. Same coarse loop as the
    // singular case — re-prompt the whole array, no per-item refix.
    [Fact]
    public async Task BoundedArray_TooFewHypotheses_AutoFixesToValidSet()
    {
        var client = new ScriptedChatClient(OneHypothesis, ThreeHypotheses);

        var r = await SchemaConstrainedEmitter.EmitAsync(
            client, "test-model", "Propose hypotheses.", ReasoningSchemas.Hypotheses, maxAttempts: 3);

        Assert.True(r.Ok);
        Assert.Equal(2, r.Attempts);
        Assert.Equal(3, r.Value!["hypotheses"]!.AsArray().Count);
    }

    // Structural guards fire before R1: a degenerate or runaway set is rejected by the
    // schema, not by a semantic invariant.
    [Fact]
    public void Bounds_CatchDegenerateAndRunawaySets()
    {
        // 0 facts — minItems 1.
        Assert.False(JsonSchemaGen.Validate(ReasoningSchemas.Facts, JsonNode.Parse("""{"facts":[]}"""), out _));

        // 1 hypothesis — kills the contrast (minItems 2).
        Assert.False(JsonSchemaGen.Validate(ReasoningSchemas.Hypotheses, JsonNode.Parse(OneHypothesis), out _));

        // 7 hypotheses — runaway fan-out (maxItems 6).
        var seven = new JsonObject
        {
            ["hypotheses"] = new JsonArray(Enumerable.Range(0, 7)
                .Select(i => (JsonNode)new JsonObject { ["statement"] = $"h{i}" }).ToArray()),
        };
        Assert.False(JsonSchemaGen.Validate(ReasoningSchemas.Hypotheses, seven, out _));

        // 3 hypotheses — within bounds.
        Assert.True(JsonSchemaGen.Validate(ReasoningSchemas.Hypotheses, JsonNode.Parse(ThreeHypotheses), out _));
    }

    // A bad item inside an otherwise well-sized array still fails structurally (each
    // item must satisfy the item schema), so the auto-fix loop catches it.
    [Fact]
    public void Bounds_ItemSchemaStillEnforcedWithinArray()
    {
        // 2 facts but one is missing sourceRef → structurally invalid.
        var json = """{"facts":[{"claim":"a","sourceKind":"doc","sourceRef":"x://1"},{"claim":"b","sourceKind":"doc"}]}""";
        Assert.False(JsonSchemaGen.Validate(ReasoningSchemas.Facts, JsonNode.Parse(json), out _));
    }
}
