using System.Text.Json.Nodes;
using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Model;
using Plexus.Sidecar.Services;

namespace Plexus.Sidecar.Tests;

// X1 — synthesis converges selected branches into a decision-brief deliverable.
// The model turn itself isn't run here (no live API); these cover the deterministic
// pieces: the harvested INPUT (union of branches), the brief OUTPUT contract (a
// decision brief is a valid catalog block array with a comparison table + a
// recommendation), and the instruction that drives it.
public class SynthesisTests
{
    [Fact]
    public void BuildHistory_over_selected_branches_unions_the_explorations()
    {
        var graph = new Graph { Id = "g" };
        graph.Nodes.Add(new Node { Id = "r", Role = "user", CreatedAt = "2026-01-01T00:00:00Z", Raw = "Pick a database" });
        graph.Nodes.Add(new Node { Id = "a", ParentId = "r", Role = "assistant", CreatedAt = "2026-01-01T00:00:01Z", Raw = "Postgres: relational, mature" });
        graph.Nodes.Add(new Node { Id = "b", ParentId = "r", Role = "assistant", CreatedAt = "2026-01-01T00:00:02Z", Raw = "SQLite: embedded, simple" });

        var history = ConversationService.BuildHistory(graph, new[] { "a", "b" });

        // Union of both branches' ancestor paths, deduped (root once), chronological.
        Assert.Equal(3, history.Count);
        Assert.Equal("Pick a database", history[0].Content);
        Assert.Contains(history, h => h.Content == "Postgres: relational, mature");
        Assert.Contains(history, h => h.Content == "SQLite: embedded, simple");
    }

    [Fact]
    public void A_decision_brief_is_a_valid_catalog_block_array_with_table_and_recommendation()
    {
        // The shape the synthesis turn is instructed to emit (emitted + validated
        // through the existing C0/C1 catalog — no new emission machinery).
        var brief = """
            [
              {"type":"markdown","text":"# Choose a logging library\n\nWhich logging library should we adopt?"},
              {"type":"markdown","text":"Options considered: Serilog, NLog, built-in."},
              {"type":"table",
                "columns":[{"key":"lib","label":"Library"},{"key":"fit","label":"Fit"},{"key":"maturity","label":"Maturity"}],
                "rows":[{"lib":"Serilog","fit":"high","maturity":"high"},{"lib":"NLog","fit":"mid","maturity":"high"}]},
              {"type":"markdown","text":"Ruled out NLog: weaker structured-logging ergonomics."},
              {"type":"markdown","text":"## Recommendation\n\nAdopt Serilog — best structured logging + mature sinks. Revisit if we standardize on OpenTelemetry."},
              {"type":"markdown","text":"## Sources\n\n- serilog.net docs"}
            ]
            """;

        Assert.True(BlockCatalog.ValidateBlocksArray(JsonNode.Parse(brief), out var errors), string.Join("; ", errors));

        var blocks = PlexusJson.Deserialize<List<Block>>(brief)!;
        Assert.Contains(blocks, b => b is TableBlock);                                       // comparison table
        Assert.Contains(blocks, b => b is MarkdownBlock m && m.Text.Contains("Recommendation")); // a recommendation
    }

    [Fact]
    public void Synthesis_instruction_asks_for_a_decision_not_a_summary()
    {
        var t = SynthesisPrompt.Instruction;
        Assert.Contains("DECISION BRIEF", t);
        Assert.Contains("table", t);
        Assert.Contains("Recommendation", t);
        Assert.Contains("Sources", t);
        // explicitly NOT a concatenation, and honest when there's no clear decision
        Assert.Contains("concatenate", t);
        Assert.Contains("honestly", t);
    }
}
