using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Mcp;
using Plexus.Sidecar.Model;

namespace Plexus.Sidecar.Tests;

// ADR-0002 R2.2.1 — the MCP-backed grounding source. Parses the catalog tool's JSON
// passage array into SourcePassages and plugs into the recipe exactly where the curated
// mock did. Tested with a stub retrieval delegate (no real MCP server); a real catalog
// server is wired the same way in production.
public class McpFactSourceTests
{
    private static McpFactSource From(string toolResult) =>
        new((_, _) => Task.FromResult(toolResult));

    [Fact]
    public async Task ParsesWellFormedPassages_WithKinds()
    {
        var src = From("""[{"id":"ctrl-1","text":"Control one.","kind":"doc"},{"id":"api-2","text":"API two.","kind":"api"}]""");

        var passages = await src.RetrieveAsync("case");

        Assert.Equal(2, passages.Count);
        Assert.Equal("ctrl-1", passages[0].Id);
        Assert.Equal(FactSources.Doc, passages[0].Kind);
        Assert.Equal(FactSources.Api, passages[1].Kind);
    }

    [Fact]
    public async Task MissingKind_DefaultsToDoc()
    {
        var src = From("""[{"id":"s1","text":"t"}]""");

        var p = Assert.Single(await src.RetrieveAsync("case"));

        Assert.Equal(FactSources.Doc, p.Kind);
    }

    [Theory]
    [InlineData("[error] MCP server 'cat' is not connected.")]
    [InlineData("[tool error] backend down")]
    [InlineData("not json at all")]
    [InlineData("")]
    public async Task ToolErrorOrUnparseable_YieldsNoSources(string toolResult)
    {
        Assert.Empty(await From(toolResult).RetrieveAsync("case"));
    }

    [Fact]
    public async Task SkipsMalformedEntries_MissingIdOrText()
    {
        var src = From("""[{"id":"ok","text":"good"},{"text":"no id"},{"id":"no-text"}]""");

        var p = Assert.Single(await src.RetrieveAsync("case"));

        Assert.Equal("ok", p.Id);
    }

    // The swap is transparent: an MCP-backed source grounds the recipe exactly like the
    // curated mock — source nodes appear, facts cite them, grounds is derived.
    [Fact]
    public async Task PlugsIntoRecipe_LikeTheMock()
    {
        var recipe = new Recipe
        {
            Id = "t",
            Steps =
            {
                new() { Id = "frame", Role = ReasoningRoles.Frame, Prompt = "frame" },
                new() { Id = "facts", Role = ReasoningRoles.Fact, Array = true, MinItems = 1, Prompt = "facts" },
            },
        };
        var client = new ScriptedChatClient(
            """{"question":"q"}""",
            """{"facts":[{"claim":"A","sourceKind":"given","sourceRef":"ctrl-1"}]}""");
        var src = From("""[{"id":"ctrl-1","text":"Control one.","kind":"doc"}]""");

        var run = await RecipeExecutor.RunAsync(client, recipe, "small", factSource: src);

        Assert.True(run.Ok);
        Assert.Contains(run.Graph.Nodes, n => n.Reasoning?.Role == ReasoningRoles.Source && n.Id == "ctrl-1");
        Assert.Single(run.Graph.Edges, e => e.Kind == ReasoningEdges.Grounds && e.To == "ctrl-1");
    }
}
