namespace Plexus.Sidecar.Contract;

// Mirror of the Graph model in contract/blocks.ts.

public sealed class Node
{
    public string Id { get; set; } = "";
    public string? ParentId { get; set; } // single parent (tree). DAG is P2.
    public string Role { get; set; } = "user"; // "user" | "assistant"
    public string CreatedAt { get; set; } = ""; // ISO-8601
    public List<Block> Blocks { get; set; } = new();
    public string Raw { get; set; } = ""; // model's original text — re-fed on resume
    public NodeMeta? Meta { get; set; }
}

public sealed class NodeMeta
{
    public string? Model { get; set; }
    public int? TokensIn { get; set; }
    public int? TokensOut { get; set; }
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
}

public sealed class GraphSummary
{
    public string Id { get; set; } = "";
    public string? Title { get; set; }
}
