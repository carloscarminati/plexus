using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Model;
using Plexus.Sidecar.Persistence;
using Plexus.Sidecar.Services;

namespace Plexus.Sidecar.Model;

// Orchestrates a turn: persist the user node, reconstruct context by walking
// ancestors (spec §4.4), call the model, enrich link cards, persist the
// assistant node. Emits server events as it goes so the UI can show progress.
public sealed class ConversationService
{
    private readonly GraphStore _store;
    private readonly AnthropicTurnService _turns;
    private readonly LinkCardResolver _linkCards;

    public ConversationService(GraphStore store, AnthropicTurnService turns, LinkCardResolver linkCards)
    {
        _store = store;
        _turns = turns;
        _linkCards = linkCards;
    }

    public async Task RunTurnAsync(
        string graphId,
        string? fromNodeId,
        string text,
        Func<ServerEvent, Task> emit,
        CancellationToken ct = default)
    {
        var graph = _store.LoadGraph(graphId)
            ?? throw new InvalidOperationException($"Graph '{graphId}' not found.");

        // 1. The user's message branches from the selected node (or root).
        var userNode = new Node
        {
            Id = Guid.NewGuid().ToString("n"),
            ParentId = fromNodeId,
            Role = "user",
            CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
            Blocks = new List<Block> { new MarkdownBlock { Text = text } },
            Raw = text,
        };
        _store.AddNode(graphId, userNode);
        graph.Nodes.Add(userNode);
        await emit(new NodeCreatedServerEvent { Node = userNode });

        // 2. Reserve the assistant node id and tell the UI a turn is underway.
        var assistantId = Guid.NewGuid().ToString("n");
        await emit(new TurnStartedServerEvent { NodeId = assistantId, ParentId = userNode.Id });

        // 3. Reconstruct context: ancestors of the user node, oldest first.
        var history = BuildHistory(graph, userNode.Id);

        // 4. Call the model.
        var result = await _turns.CompleteAsync(new TurnRequest(history), ct);

        // 5. Resolve OG images for any link cards (server-side, spec P0).
        await _linkCards.EnrichAsync(result.Blocks, ct);

        // 6. Persist and emit the assistant node as a child of the user node.
        var assistantNode = new Node
        {
            Id = assistantId,
            ParentId = userNode.Id,
            Role = "assistant",
            CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
            Blocks = result.Blocks,
            Raw = result.Raw,
            Meta = new NodeMeta { Model = result.Model, TokensIn = result.TokensIn, TokensOut = result.TokensOut },
        };
        _store.AddNode(graphId, assistantNode);
        await emit(new TurnCompletedServerEvent { Node = assistantNode });
    }

    // Walk from `nodeId` to the root following parentId, then order by createdAt
    // so the model sees the conversation in chronological order. User turns are
    // sent as-is; assistant turns use their stored raw text (not a re-render).
    public static List<(string Role, string Content)> BuildHistory(Graph graph, string nodeId)
    {
        var byId = graph.Nodes.ToDictionary(n => n.Id);
        var chain = new List<Node>();
        string? cursor = nodeId;
        while (cursor is not null && byId.TryGetValue(cursor, out var node))
        {
            chain.Add(node);
            cursor = node.ParentId;
        }

        chain.Reverse(); // root -> leaf
        chain.Sort((a, b) => string.CompareOrdinal(a.CreatedAt, b.CreatedAt));

        return chain.Select(n => (n.Role, n.Raw)).ToList();
    }
}
