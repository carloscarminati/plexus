using Plexus.Sidecar.Routing;

namespace Plexus.Sidecar.Contract;

// Mirror of the Graph model in contract/blocks.ts.

public sealed class Node
{
    public string Id { get; set; } = "";
    public string? ParentId { get; set; } // primary parent
    public List<string>? MergeParents { get; set; } // P2 DAG merge: extra parents
    public string Role { get; set; } = "user"; // "user" | "assistant"
    public string? Kind { get; set; } // X1: "deliverable" (synthesis brief) — null otherwise
    public string CreatedAt { get; set; } = ""; // ISO-8601
    public List<Block> Blocks { get; set; } = new();
    public string Raw { get; set; } = ""; // model's original text — re-fed on resume
    public NodeMeta? Meta { get; set; }
}

public sealed class NodeMeta
{
    public string? Model { get; set; }
    public string? ProviderId { get; set; }
    public int? TokensIn { get; set; }
    public int? TokensOut { get; set; }
    public double? CostUsd { get; set; }
    public long? LatencyMs { get; set; }
    public string? Reason { get; set; }  // why this model was picked (router)
    public string? Policy { get; set; }  // canonical effective policy ("auto:cost", "manual:<id>")
    public List<ToolCallRecord>? ToolCalls { get; set; } // M0: MCP tool invocations this turn
}

// A single MCP tool invocation within an assistant turn (shown in the node).
public sealed class ToolCallRecord
{
    public string ServerId { get; set; } = "";
    public string Tool { get; set; } = "";
    public System.Text.Json.JsonElement Args { get; set; }
    public string ResultSummary { get; set; } = "";
    public bool ReadOnly { get; set; }
    public bool Approved { get; set; }
}

public sealed class Edge
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

public sealed class Graph
{
    public string Id { get; set; } = "";
    public string? Title { get; set; }
    public List<Node> Nodes { get; set; } = new();
    public List<Edge> Edges { get; set; } = new();
    public RoutingPolicy? DefaultPolicy { get; set; } // session default routing policy
}

public sealed class GraphSummary
{
    public string Id { get; set; } = "";
    public string? Title { get; set; }
    public string? UpdatedAt { get; set; } // ISO-8601: latest node, else graph created_at
    public bool Pinned { get; set; } // sticks to the top of the sidebar
}
