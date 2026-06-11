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
[JsonDerivedType(typeof(ToolConfirmationEvent), "tool_confirmation")]
[JsonDerivedType(typeof(EscalateEvent), "escalate")]
[JsonDerivedType(typeof(SynthesizeEvent), "synthesize")]
[JsonDerivedType(typeof(SetGraphTitleEvent), "set_graph_title")]
[JsonDerivedType(typeof(SetGraphPinnedEvent), "set_graph_pinned")]
[JsonDerivedType(typeof(DeleteGraphEvent), "delete_graph")]
[JsonDerivedType(typeof(GetSettingsEvent), "get_settings")]
[JsonDerivedType(typeof(SetGeneralSettingsEvent), "set_general_settings")]
[JsonDerivedType(typeof(SetDefaultPolicyEvent), "set_default_policy")]
[JsonDerivedType(typeof(SetAnthropicKeyEvent), "set_anthropic_key")]
[JsonDerivedType(typeof(DeleteAnthropicKeyEvent), "delete_anthropic_key")]
[JsonDerivedType(typeof(SetMcpServerEvent), "set_mcp_server")]
[JsonDerivedType(typeof(DeleteMcpServerEvent), "delete_mcp_server")]
[JsonDerivedType(typeof(SetProviderEvent), "set_provider")]
[JsonDerivedType(typeof(DeleteProviderEvent), "delete_provider")]
[JsonDerivedType(typeof(RunRecipeDevEvent), "dev_run_recipe")]
public abstract class ClientEvent { }

// DEV/skeleton trigger (ADR-0002 Rx) — NOT a product flow. Runs a reasoning recipe over
// raw case text and persists the graph, to prove the engine is reachable from the real
// event+persistence surface. Deliberately decoupled: raw caseText only (no conversation-
// node linkage — how the two graph layers relate is a later decision), mock grounding.
// The "dev_" discriminator marks it; it must not be wired into any user flow.
public sealed class RunRecipeDevEvent : ClientEvent
{
    public string? RecipeId { get; set; } // null/empty = investigator
    public string CaseText { get; set; } = "";
}

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

public sealed class ToolConfirmationEvent : ClientEvent // M0 — user's decision on a gated tool call
{
    public string ToolUseId { get; set; } = "";
    public bool Approved { get; set; }
}

// R1 §4.2 — re-run the input that produced an assistant node with a (usually
// stronger) model as a SIBLING branch (same parent), for side-by-side compare.
public sealed class EscalateEvent : ClientEvent
{
    public string GraphId { get; set; } = "";
    public string NodeId { get; set; } = ""; // the assistant node to escalate
    public RoutingPolicy? Policy { get; set; } // default: auto:quality (top tier)
}

// X1 — synthesize the selected branches into a decision-brief deliverable node.
public sealed class SynthesizeEvent : ClientEvent
{
    public string GraphId { get; set; } = "";
    public List<string> FromNodeIds { get; set; } = new(); // the selected branches
    public RoutingPolicy? Policy { get; set; }
}

// Graph management — rename a graph (inline title edit).
public sealed class SetGraphTitleEvent : ClientEvent
{
    public string GraphId { get; set; } = "";
    public string? Title { get; set; }
}

// Graph management — pin/unpin a conversation (sticks to the top of the sidebar).
public sealed class SetGraphPinnedEvent : ClientEvent
{
    public string GraphId { get; set; } = "";
    public bool Pinned { get; set; }
}

// Graph management — delete a graph and its nodes (destructive; UI confirms).
public sealed class DeleteGraphEvent : ClientEvent
{
    public string GraphId { get; set; } = "";
}

// ── Settings (consolidates config the UI surfaces) ───────────────────────────
public sealed class GetSettingsEvent : ClientEvent { }

public sealed class SetGeneralSettingsEvent : ClientEvent
{
    public int ConfirmTimeoutSeconds { get; set; } = 120;
}

public sealed class SetDefaultPolicyEvent : ClientEvent
{
    public RoutingPolicy Policy { get; set; } = RoutingPolicy.Manual("claude-opus-4-8");
}

// Providers (Anthropic-only execution): the key goes to the keychain, never a file.
public sealed class SetAnthropicKeyEvent : ClientEvent
{
    public string Key { get; set; } = "";
}

public sealed class DeleteAnthropicKeyEvent : ClientEvent { }

// MCP registry edits. The HTTP credential (if any) is keychain-bound by id; the
// rest of the config is written to mcp-servers.json. Never put the secret in the file.
public sealed class SetMcpServerEvent : ClientEvent
{
    public McpServerView Server { get; set; } = new();
    public string? HttpCredential { get; set; }
}

public sealed class DeleteMcpServerEvent : ClientEvent
{
    public string Id { get; set; } = "";
}

// Secret-free view of an MCP server (mirror of McpServerConfig + a credential flag).
public sealed class McpServerView
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public McpTransportView Transport { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public string? ToolPolicy { get; set; }
    public bool HttpCredentialSet { get; set; } // true if a keychain credential exists
}

public sealed class McpTransportView
{
    public string Kind { get; set; } = "stdio"; // "stdio" | "http"
    public string? Command { get; set; }
    public List<string>? Args { get; set; }
    public Dictionary<string, string>? Env { get; set; }
    public string? Url { get; set; }
}

// Providers CRUD (mirror of the MCP-server pattern). The non-secret config is
// written to providers.json; the API key goes to the keychain by provider id,
// never the file. Anthropic = the migrated default; openai-compatible = base URL
// + model id + key (works with OpenAI, gateways, Ollama, any compatible server).
public sealed class SetProviderEvent : ClientEvent
{
    public ProviderView Provider { get; set; } = new();
    public string? ApiKey { get; set; } // optional; only present when (re)setting the key
}

public sealed class DeleteProviderEvent : ClientEvent
{
    public string Id { get; set; } = "";
}

// Secret-free view of a provider (mirror of ProviderConfig + a key-present flag).
public sealed class ProviderView
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "anthropic"; // "anthropic" | "openai-compatible"
    public string? Label { get; set; }
    public string? BaseUrl { get; set; }            // openai-compatible only
    public string? ModelId { get; set; }            // default model for the picker
    public bool Enabled { get; set; } = true;
    public bool KeySet { get; set; }                // true if a keychain key exists for this id
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(GraphsServerEvent), "graphs")]
[JsonDerivedType(typeof(GraphServerEvent), "graph")]
[JsonDerivedType(typeof(NodeCreatedServerEvent), "node_created")]
[JsonDerivedType(typeof(TurnStartedServerEvent), "turn_started")]
[JsonDerivedType(typeof(TurnDeltaServerEvent), "turn_delta")]
[JsonDerivedType(typeof(TurnCompletedServerEvent), "turn_completed")]
[JsonDerivedType(typeof(ModelsServerEvent), "models")]
[JsonDerivedType(typeof(ToolConfirmationRequestServerEvent), "tool_confirmation_request")]
[JsonDerivedType(typeof(SettingsServerEvent), "settings")]
[JsonDerivedType(typeof(ErrorServerEvent), "error")]
[JsonDerivedType(typeof(RecipeRunDoneServerEvent), "recipe_run_done")]
public abstract class ServerEvent { }

// ADR-0002 Rx — the dev recipe run finished; the graph is persisted under GraphId. No
// node-by-node streaming (a done event is enough for the skeleton).
public sealed class RecipeRunDoneServerEvent : ServerEvent
{
    public string GraphId { get; set; } = "";
}

// Consolidated, secret-free config snapshot for the Settings panel.
public sealed class SettingsServerEvent : ServerEvent
{
    public int ConfirmTimeoutSeconds { get; set; }
    public RoutingPolicy DefaultPolicy { get; set; } = RoutingPolicy.Manual("claude-opus-4-8");
    public bool AnthropicKeyConfigured { get; set; }
    public List<McpServerView> McpServers { get; set; } = new();
    public List<ProviderView> Providers { get; set; } = new();
}

// M0 — the host asks the user to approve a non-read-only MCP tool call.
public sealed class ToolConfirmationRequestServerEvent : ServerEvent
{
    public string NodeId { get; set; } = "";
    public string ToolUseId { get; set; } = "";
    public string ServerId { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string Tool { get; set; } = "";
    public System.Text.Json.JsonElement Args { get; set; }
    public bool ReadOnly { get; set; }
}

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
