using System.Diagnostics;
using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Model;
using Plexus.Sidecar.Persistence;
using Plexus.Sidecar.Routing;
using Plexus.Sidecar.Services;

namespace Plexus.Sidecar.Model;

// Orchestrates a turn: persist the user node, reconstruct context by walking
// ancestors (spec §4.4), pick a model via the router, call it, enrich link
// cards, record telemetry, persist the assistant node. Emits server events as
// it goes so the UI can show progress.
public sealed class ConversationService
{
    // R0 session default. Per-node override is stored in node.meta (a node's
    // model becomes the sticky default for turns branching off it).
    private const string DefaultModel = "claude-opus-4-8";

    private readonly GraphStore _store;
    private readonly AnthropicTurnService _turns;
    private readonly LinkCardResolver _linkCards;
    private readonly IModelRouter _router;
    private readonly ModelRegistry _registry;
    private readonly ITelemetrySink _telemetry;

    public ConversationService(
        GraphStore store,
        AnthropicTurnService turns,
        LinkCardResolver linkCards,
        IModelRouter router,
        ModelRegistry registry,
        ITelemetrySink telemetry)
    {
        _store = store;
        _turns = turns;
        _linkCards = linkCards;
        _router = router;
        _registry = registry;
        _telemetry = telemetry;
    }

    public async Task RunTurnAsync(
        string graphId,
        string? fromNodeId,
        string text,
        Func<ServerEvent, Task> emit,
        RoutingPolicy? requestedPolicy = null,
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

        // 4. Resolve the effective policy and pick a model.
        var requires = new RequestRequirements(
            StructuredOutput: true, // always — block emission strategy (a), SPEC.md §4.2
            MinContext: EstimateTokens(history));

        // Effective policy: explicit request (UI: node override ?? session default),
        // else the graph's persisted session default, else manual large.
        var effective = requestedPolicy ?? graph.DefaultPolicy ?? RoutingPolicy.Manual(DefaultModel);
        var canonical = Canonical(effective);

        // Branch-level stickiness (§4.1): keep the branch's model unless the policy
        // changed or the sticky model can no longer meet `requires` (e.g. now needs
        // vision). Manual policies skip stickiness — the model is explicit and wins.
        var (branchModel, branchProvider, branchPolicyCanon) = NearestAssistantRouting(graph, fromNodeId);
        ModelChoice choice;
        if (effective.Kind == "auto"
            && branchModel is not null
            && branchPolicyCanon == canonical
            && CanReuse(branchProvider ?? _registry.DefaultProviderId, branchModel, requires))
        {
            choice = new ModelChoice(branchModel, branchProvider ?? _registry.DefaultProviderId,
                $"{canonical}: sticky branch model ({branchModel})");
        }
        else
        {
            choice = await _router.SelectModelAsync(new RoutingContext(history, requires, effective), ct);
        }

        // 5. Call the model, measuring latency.
        var sw = Stopwatch.StartNew();
        var result = await _turns.CompleteAsync(new TurnRequest(history), choice.ModelId, ct);
        sw.Stop();

        // 6. Resolve OG images for any link cards (server-side, spec P0).
        await _linkCards.EnrichAsync(result.Blocks, ct);

        // 7. Cost + telemetry (real policy/reason; telemetry schema unchanged).
        var cost = _registry.EstimateCostUsd(choice.ProviderId, choice.ModelId, result.TokensIn, result.TokensOut);
        _telemetry.Record(new TelemetryRecord(
            DateTimeOffset.UtcNow.ToString("o"), graphId, assistantId,
            choice.ModelId, choice.ProviderId, result.TokensIn, result.TokensOut,
            cost, sw.ElapsedMilliseconds, canonical, choice.Reason));

        // 8. Persist and emit the assistant node as a child of the user node.
        var assistantNode = new Node
        {
            Id = assistantId,
            ParentId = userNode.Id,
            Role = "assistant",
            CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
            Blocks = result.Blocks,
            Raw = result.Raw,
            Meta = new NodeMeta
            {
                Model = choice.ModelId,
                ProviderId = choice.ProviderId,
                TokensIn = result.TokensIn,
                TokensOut = result.TokensOut,
                CostUsd = cost,
                LatencyMs = sw.ElapsedMilliseconds,
                Reason = choice.Reason,
                Policy = canonical,
            },
        };
        _store.AddNode(graphId, assistantNode);
        await emit(new TurnCompletedServerEvent { Node = assistantNode });
    }

    private static string Canonical(RoutingPolicy? p) =>
        p is null ? "" : p.Kind == "manual" ? $"manual:{p.ModelId}" : $"auto:{p.Objective}";

    // The nearest assistant node's (model, providerId, canonical policy) on the
    // path to root.
    private static (string? Model, string? Provider, string? PolicyCanon) NearestAssistantRouting(Graph graph, string? fromNodeId)
    {
        var byId = graph.Nodes.ToDictionary(n => n.Id);
        var cursor = fromNodeId;
        while (cursor is not null && byId.TryGetValue(cursor, out var node))
        {
            if (node.Role == "assistant" && !string.IsNullOrEmpty(node.Meta?.Model))
                return (node.Meta!.Model, node.Meta.ProviderId, node.Meta.Policy);
            cursor = node.ParentId;
        }
        return (null, null, null);
    }

    private bool CanReuse(string providerId, string modelId, RequestRequirements requires)
    {
        var meta = _registry.GetMetadata(providerId, modelId);
        return meta is null || ManualRouter.Unmet(meta, requires).Count == 0;
    }

    // Cheap pre-call token estimate (~4 chars/token) for the minContext guardrail.
    private static int EstimateTokens(IReadOnlyList<(string Role, string Content)> history)
        => history.Sum(h => h.Content.Length) / 4;

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
