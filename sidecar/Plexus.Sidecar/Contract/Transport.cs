using System.Text.Json;
using System.Text.Json.Serialization;

namespace Plexus.Sidecar.Contract;

// Local-WebSocket protocol. Mirror of ClientEvent / ServerEvent in
// contract/blocks.ts. The frontend renders, never thinks: it sends intents,
// the sidecar owns all state.

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(LoadGraphEvent), "load_graph")]
[JsonDerivedType(typeof(ListGraphsEvent), "list_graphs")]
[JsonDerivedType(typeof(NewGraphEvent), "new_graph")]
[JsonDerivedType(typeof(SendMessageEvent), "send_message")]
[JsonDerivedType(typeof(IntentEvent), "intent")]
public abstract class ClientEvent { }

public sealed class LoadGraphEvent : ClientEvent
{
    public string GraphId { get; set; } = "";
}

public sealed class ListGraphsEvent : ClientEvent { }

public sealed class NewGraphEvent : ClientEvent
{
    public string? Title { get; set; }
}

public sealed class SendMessageEvent : ClientEvent
{
    public string GraphId { get; set; } = "";
    public string? FromNodeId { get; set; } // null = start of a fresh graph
    public string Text { get; set; } = "";
}

public sealed class IntentEvent : ClientEvent // P1
{
    public string GraphId { get; set; } = "";
    public string NodeId { get; set; } = "";
    public string Kind { get; set; } = "";
    public JsonElement Payload { get; set; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(GraphsServerEvent), "graphs")]
[JsonDerivedType(typeof(GraphServerEvent), "graph")]
[JsonDerivedType(typeof(NodeCreatedServerEvent), "node_created")]
[JsonDerivedType(typeof(TurnStartedServerEvent), "turn_started")]
[JsonDerivedType(typeof(TurnDeltaServerEvent), "turn_delta")]
[JsonDerivedType(typeof(TurnCompletedServerEvent), "turn_completed")]
[JsonDerivedType(typeof(ErrorServerEvent), "error")]
public abstract class ServerEvent { }

public sealed class GraphsServerEvent : ServerEvent
{
    public List<GraphSummary> Graphs { get; set; } = new();
}

public sealed class GraphServerEvent : ServerEvent
{
    public Graph Graph { get; set; } = new();
}

public sealed class NodeCreatedServerEvent : ServerEvent
{
    public Node Node { get; set; } = new();
}

public sealed class TurnStartedServerEvent : ServerEvent
{
    public string NodeId { get; set; } = "";
    public string? ParentId { get; set; }
}

public sealed class TurnDeltaServerEvent : ServerEvent // P1 — progressive render
{
    public string NodeId { get; set; } = "";
    public List<Block> Blocks { get; set; } = new();
}

public sealed class TurnCompletedServerEvent : ServerEvent
{
    public Node Node { get; set; } = new();
}

public sealed class ErrorServerEvent : ServerEvent
{
    public string Message { get; set; } = "";
}
