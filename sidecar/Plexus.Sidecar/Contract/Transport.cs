using System.Text.Json;
using System.Text.Json.Serialization;
using Plexus.Sidecar.Routing;

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
[JsonDerivedType(typeof(SetSessionPolicyEvent), "set_session_policy")]
[JsonDerivedType(typeof(ListModelsEvent), "list_models")]
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
    public List<string>? FromNodeIds { get; set; } // P2 DAG merge: ≥2 → union-of-ancestors
    public string Text { get; set; } = "";
    public RoutingPolicy? Policy { get; set; } // resolved (node override ?? session default)
}

public sealed class IntentEvent : ClientEvent // P1
{
    public string GraphId { get; set; } = "";
    public string NodeId { get; set; } = "";
    public string Kind { get; set; } = "";
    public JsonElement Payload { get; set; }
    public RoutingPolicy? Policy { get; set; }
}

public sealed class SetSessionPolicyEvent : ClientEvent // R1 — persist the session default
{
    public string GraphId { get; set; } = "";
    public RoutingPolicy Policy { get; set; } = RoutingPolicy.Manual("claude-opus-4-8");
}

public sealed class ListModelsEvent : ClientEvent { } // R1 — request the curated candidate set

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(GraphsServerEvent), "graphs")]
[JsonDerivedType(typeof(GraphServerEvent), "graph")]
[JsonDerivedType(typeof(NodeCreatedServerEvent), "node_created")]
[JsonDerivedType(typeof(TurnStartedServerEvent), "turn_started")]
[JsonDerivedType(typeof(TurnDeltaServerEvent), "turn_delta")]
[JsonDerivedType(typeof(TurnCompletedServerEvent), "turn_completed")]
[JsonDerivedType(typeof(ModelsServerEvent), "models")]
[JsonDerivedType(typeof(ErrorServerEvent), "error")]
public abstract class ServerEvent { }

// One curated candidate model (R1), with metadata for the Manual picker.
public sealed class ModelInfo
{
    public string Id { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string Tier { get; set; } = ""; // "small" | "mid" | "large"
    public double CostInPerMTok { get; set; }
    public double CostOutPerMTok { get; set; }
    public int ContextWindow { get; set; }
    public bool ToolCall { get; set; }
    public bool Vision { get; set; }
}

public sealed class ModelsServerEvent : ServerEvent
{
    public List<ModelInfo> Models { get; set; } = new();
}

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
