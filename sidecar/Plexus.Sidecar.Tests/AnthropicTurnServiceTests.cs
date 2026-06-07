using System.Reflection;
using Anthropic.Models.Messages;
using Plexus.Sidecar.Model;

namespace Plexus.Sidecar.Tests;

// Regression guard for the tool-free turn bug: with zero MCP servers configured,
// `toolUnions` is null. `ToolChoice` is a union with an implicit conversion from
// `ToolChoiceAuto`, so the tempting `ToolChoice = tools is not null ? new …() : null`
// runs the null THROUGH that operator and serializes `"tool_choice":null`, which the
// API rejects with 400 "tool_choice: Input should be an object". BuildMessageParams
// must therefore OMIT the tool fields entirely when there are no tools.
//
// We assert on the EXACT bytes the SDK would send: ParamsBase.BodyContent() is the
// HTTP body builder used by the real client, invoked here via reflection.
public class AnthropicTurnServiceTests
{
    private static string RequestBody(MessageCreateParams p)
    {
        // ParamsBase.BodyContent() -> HttpContent (the JSON sent to the API).
        var method = typeof(MessageCreateParams).BaseType! // Anthropic.Core.ParamsBase
            .GetMethod("BodyContent", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("ParamsBase.BodyContent not found — SDK internals changed.");
        var content = (HttpContent)method.Invoke(p, null)!;
        return content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    private static List<MessageParam> SampleHistory() => new()
    {
        new() { Role = Role.User, Content = "What is a vector database?" },
    };

    [Fact]
    public void ToolFreeTurn_DoesNotSendToolChoice()
    {
        // A turn with no MCP servers: toolUnions is null.
        var p = AnthropicTurnService.BuildMessageParams(
            "claude-haiku-4-5", SampleHistory(), advanced: false, toolUnions: null);

        var body = RequestBody(p);

        // The exact failure mode: the body must not carry tool_choice at all
        // (a null would be rejected with 400 by the API).
        Assert.DoesNotContain("tool_choice", body);
        Assert.DoesNotContain("\"tools\"", body);
    }

    [Fact]
    public void ToolTurn_SendsToolChoiceObject()
    {
        var tool = new Tool
        {
            Name = "get_time",
            Description = "Returns the current time.",
            InputSchema = new() { Properties = new Dictionary<string, System.Text.Json.JsonElement>() },
        };
        var toolUnions = new List<ToolUnion> { tool };

        var p = AnthropicTurnService.BuildMessageParams(
            "claude-opus-4-8", SampleHistory(), advanced: true, toolUnions: toolUnions);

        var body = RequestBody(p);

        // When tools exist, tool_choice IS present and is an object (auto), and the
        // round runs one tool at a time (disable_parallel_tool_use).
        Assert.Contains("\"tool_choice\":{", body);
        Assert.Contains("disable_parallel_tool_use", body);
        Assert.Contains("\"tools\":[", body);
    }
}
