using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Mcp;
using Plexus.Sidecar.Model;
using Plexus.Sidecar.Persistence;
using Plexus.Sidecar.Routing;
using Plexus.Sidecar.Services;

namespace Plexus.Sidecar.Model;

// Surfaced to the WebSocket layer so it can show the user a tool call and await
// their decision before the host executes it (M0 §3.2).
public sealed record ToolConfirmRequest(
    string NodeId, string ToolUseId, string ServerId, string ServerName, string Tool, JsonElement Args, bool ReadOnly);

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
    private readonly ChatTurnService _chatTurns;
    private readonly ChatClientFactory _clientFactory;
    private readonly LinkCardResolver _linkCards;
    private readonly IModelRouter _router;
    private readonly ModelRegistry _registry;
    private readonly ITelemetrySink _telemetry;
    private readonly Mcp.McpHost _mcp;

    public ConversationService(
        GraphStore store,
        ChatTurnService chatTurns,
        ChatClientFactory clientFactory,
        LinkCardResolver linkCards,
        IModelRouter router,
        ModelRegistry registry,
        ITelemetrySink telemetry,
        Mcp.McpHost mcp)
    {
        _store = store;
        _chatTurns = chatTurns;
        _clientFactory = clientFactory;
        _linkCards = linkCards;
        _router = router;
        _registry = registry;
        _telemetry = telemetry;
        _mcp = mcp;
    }

    public async Task RunTurnAsync(
        string graphId,
        string? fromNodeId,
        string text,
        Func<ServerEvent, Task> emit,
        RoutingPolicy? requestedPolicy = null,
        IReadOnlyList<string>? fromNodeIds = null,
        Func<ToolConfirmRequest, Task<bool>>? confirmTool = null,
        CancellationToken ct = default)
    {
        var graph = _store.LoadGraph(graphId)
            ?? throw new InvalidOperationException($"Graph '{graphId}' not found.");

        // Resolve the parent set. fromNodeIds (≥2) = P2 DAG merge: the user node
        // gets multiple parents and its context is the union of all their ancestor
        // paths. Otherwise it's a normal single-parent (tree) turn.
        var parents = (fromNodeIds is { Count: > 0 })
            ? fromNodeIds.Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList()
            : (fromNodeId is null ? new List<string>() : new List<string> { fromNodeId });
        var primary = parents.Count > 0 ? parents[0] : null;
        var mergeParents = parents.Count > 1 ? parents.Skip(1).ToList() : null;

        // 1. The user's message branches from the selected node(s) (or root).
        var userNode = new Node
        {
            Id = Guid.NewGuid().ToString("n"),
            ParentId = primary,
            MergeParents = mergeParents,
            Role = "user",
            CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
            Blocks = new List<Block> { new MarkdownBlock { Text = text } },
            Raw = text,
        };
        _store.AddNode(graphId, userNode);
        graph.Nodes.Add(userNode);
        await emit(new NodeCreatedServerEvent { Node = userNode });

        // Derive the graph title from the first user message, until one exists.
        // A manual rename sets a title, so this won't clobber it afterwards.
        if (string.IsNullOrWhiteSpace(graph.Title))
        {
            var title = DeriveTitle(text);
            _store.SetGraphTitle(graphId, title);
            graph.Title = title;
            await emit(new GraphsServerEvent { Graphs = _store.ListGraphs() });
        }

        // The assistant node is a child of the user node; its context is the user
        // node's ancestor path; stickiness looks at the branch we came from.
        await GenerateAssistantAsync(
            graph, graphId, BuildHistory(graph, userNode.Id),
            parentId: userNode.Id, mergeParents: null, stickyFromNodeId: primary,
            requestedPolicy, kind: null, emit, confirmTool, ct);
    }

    // R1 §4.2 "Escalate": re-run the input that produced an assistant node with a
    // (usually stronger) model as a SIBLING branch — same parent, no new user node,
    // identical context (same ancestor path) — so the two answers sit side by side.
    public async Task EscalateTurnAsync(
        string graphId,
        string assistantNodeId,
        Func<ServerEvent, Task> emit,
        RoutingPolicy? requestedPolicy = null,
        Func<ToolConfirmRequest, Task<bool>>? confirmTool = null,
        CancellationToken ct = default)
    {
        var graph = _store.LoadGraph(graphId)
            ?? throw new InvalidOperationException($"Graph '{graphId}' not found.");

        var original = graph.Nodes.FirstOrDefault(n => n.Id == assistantNodeId)
            ?? throw new InvalidOperationException($"Node '{assistantNodeId}' not found.");
        if (original.Role != "assistant" || original.ParentId is null)
            throw new InvalidOperationException("Escalate applies to an assistant node produced from a user turn.");

        // The user node that produced it. The escalated answer becomes its sibling
        // (same parent), and re-uses its exact ancestor path as context.
        var userNode = graph.Nodes.FirstOrDefault(n => n.Id == original.ParentId)
            ?? throw new InvalidOperationException("The escalated node's parent is missing.");

        // Default escalation target: Auto-quality (top tier). The caller may pass a
        // specific model/policy instead (PolicyPicker override).
        var policy = requestedPolicy ?? RoutingPolicy.Auto("quality");

        await GenerateAssistantAsync(
            graph, graphId, BuildHistory(graph, userNode.Id),
            parentId: userNode.Id, mergeParents: null, stickyFromNodeId: userNode.ParentId,
            policy, kind: null, emit, confirmTool, ct);
    }

    // X1 "Synthesize decision brief": converge the selected branches into a new,
    // distinguished deliverable node. Context = the union of the selected nodes'
    // ancestor paths (the exploration) + a synthesis instruction; output = a
    // decision-brief block array emitted + validated through the existing catalog.
    // The node is convergent over the selection (parents = selected, reusing the P2
    // DAG-merge node shape) and tagged kind="deliverable".
    public async Task SynthesizeAsync(
        string graphId,
        IReadOnlyList<string> fromNodeIds,
        Func<ServerEvent, Task> emit,
        RoutingPolicy? requestedPolicy = null,
        Func<ToolConfirmRequest, Task<bool>>? confirmTool = null,
        CancellationToken ct = default)
    {
        var graph = _store.LoadGraph(graphId)
            ?? throw new InvalidOperationException($"Graph '{graphId}' not found.");

        var selected = (fromNodeIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrEmpty(id) && graph.Nodes.Any(n => n.Id == id))
            .Distinct()
            .ToList();
        if (selected.Count == 0)
            throw new InvalidOperationException("Synthesis needs at least one selected node.");

        var primary = selected[0];
        var mergeParents = selected.Count > 1 ? selected.Skip(1).ToList() : null;

        // Context = the union of the selected branches (the exploration), then the
        // synthesis instruction as the final user turn (ephemeral — not a stored node,
        // so the deliverable hangs directly off the selected nodes).
        var history = new List<(string Role, string Content)>(BuildHistory(graph, selected))
        {
            ("user", SynthesisPrompt.Instruction),
        };

        await GenerateAssistantAsync(
            graph, graphId, history,
            parentId: primary, mergeParents: mergeParents, stickyFromNodeId: primary,
            requestedPolicy, kind: "deliverable", emit, confirmTool, ct);
    }

    // Shared model-call + persist path for a normal turn, an Escalate sibling, and a
    // Synthesis deliverable. Takes the reconstructed `history`, picks a model, runs the
    // (gated) tool loop, records telemetry, and persists the produced node under
    // `parentId` (+ `mergeParents` for a convergent node), tagged with `kind`.
    // `stickyFromNodeId` drives branch stickiness (§4.1).
    private async Task GenerateAssistantAsync(
        Graph graph,
        string graphId,
        IReadOnlyList<(string Role, string Content)> history,
        string? parentId,
        List<string>? mergeParents,
        string? stickyFromNodeId,
        RoutingPolicy? requestedPolicy,
        string? kind,
        Func<ServerEvent, Task> emit,
        Func<ToolConfirmRequest, Task<bool>>? confirmTool,
        CancellationToken ct)
    {
        // 2. Reserve the node id and tell the UI a turn is underway.
        var assistantId = Guid.NewGuid().ToString("n");
        await emit(new TurnStartedServerEvent { NodeId = assistantId, ParentId = parentId });

        // 3b. MCP tools (M0): expose discovered tools to the model as AIFunctions —
        //     McpClientTool already derives from AIFunction, so they pass straight into
        //     the provider-generic loop. If any tool is available the turn requires a
        //     tool-capable model (drives R1's capability filter).
        var toolMap = new Dictionary<string, McpToolRef>(StringComparer.Ordinal);
        var aiTools = new List<AITool>();
        foreach (var t in _mcp.Tools)
        {
            toolMap[t.Tool.Name] = t; // dispatch the model's call back to its server
            aiTools.Add(t.Tool);
        }

        // 4. Resolve the effective policy and pick a model.
        var requires = new RequestRequirements(
            ToolCall: aiTools.Count > 0,
            StructuredOutput: true, // always — block emission strategy (a), docs/spec.md §4.2
            MinContext: EstimateTokens(history));

        // Effective policy: explicit request (UI: node override ?? session default),
        // else the graph's persisted session default, else manual large.
        var effective = requestedPolicy ?? graph.DefaultPolicy ?? RoutingPolicy.Manual(DefaultModel);
        var canonical = Canonical(effective);

        // Branch-level stickiness (§4.1): keep the branch's model unless the policy
        // changed or the sticky model can no longer meet `requires` (e.g. now needs
        // vision). Manual policies skip stickiness — the model is explicit and wins.
        var (branchModel, branchProvider, branchPolicyCanon) = NearestAssistantRouting(graph, stickyFromNodeId);
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

        // 5. Call the model, measuring latency. The executor runs MCP tools through
        //    the safety gate (M0 §3.2): read-only auto-runs; anything else (or a
        //    confirm-all server) needs explicit user confirmation before execution.
        var toolCalls = new List<ToolCallRecord>();

        async Task<string> ExecuteTool(string callId, string toolName, IReadOnlyDictionary<string, object?> args, CancellationToken c)
        {
            if (!toolMap.TryGetValue(toolName, out var t))
                return $"[error] unknown tool '{toolName}'.";

            var argsElement = JsonSerializer.SerializeToElement(args); // for the gate display + the record
            var readOnly = t.ReadOnly;
            // Conservative floor: confirm anything not explicitly read-only.
            // `confirm-all` tightens to confirm everything; no policy loosens below this.
            var needConfirm = string.Equals(t.ServerPolicy, "confirm-all", StringComparison.OrdinalIgnoreCase) || !readOnly;

            var approved = true;
            if (needConfirm)
                approved = confirmTool is not null &&
                    await confirmTool(new ToolConfirmRequest(assistantId, callId, t.ServerId, t.ServerName, t.Tool.Name, argsElement, readOnly));

            if (!approved)
            {
                toolCalls.Add(new ToolCallRecord { ServerId = t.ServerId, Tool = t.Tool.Name, Args = argsElement, ResultSummary = "(denied by the user)", ReadOnly = readOnly, Approved = false });
                return "The user denied this tool call. Do not retry it; continue without it.";
            }

            var resultText = await _mcp.CallAsync(t.ServerId, t.Tool.Name, ToArgsDict(args), c);
            toolCalls.Add(new ToolCallRecord { ServerId = t.ServerId, Tool = t.Tool.Name, Args = argsElement, ResultSummary = TruncateText(resultText, 400), ReadOnly = readOnly, Approved = true });
            return resultText;
        }

        IReadOnlyList<AITool>? toolList = aiTools.Count > 0 ? aiTools : null;
        ChatTurnService.ToolExecutor? executor = aiTools.Count > 0 ? ExecuteTool : null;

        // The factory builds the IChatClient for the routed provider (Anthropic today,
        // OpenAI-compatible once configured); the generic loop runs the same on each.
        var client = _clientFactory.For(choice.ProviderId, choice.ModelId);
        var sw = Stopwatch.StartNew();
        var result = await _chatTurns.CompleteAsync(client, choice.ModelId, new TurnRequest(history), toolList, executor, ct);
        sw.Stop();

        // 6. Resolve OG images for any link cards (server-side, spec P0).
        await _linkCards.EnrichAsync(result.Blocks, ct);

        // 7. Cost + telemetry (real policy/reason; telemetry schema unchanged).
        var cost = _registry.EstimateCostUsd(choice.ProviderId, choice.ModelId, result.TokensIn, result.TokensOut);
        _telemetry.Record(new TelemetryRecord(
            DateTimeOffset.UtcNow.ToString("o"), graphId, assistantId,
            choice.ModelId, choice.ProviderId, result.TokensIn, result.TokensOut,
            cost, sw.ElapsedMilliseconds, canonical, choice.Reason));

        // 8. Persist and emit the produced node under its parent(s). For Synthesis it
        //    is convergent over the selected nodes (mergeParents) and tagged deliverable.
        var assistantNode = new Node
        {
            Id = assistantId,
            ParentId = parentId,
            MergeParents = mergeParents,
            Kind = kind,
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
                ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
            },
        };
        _store.AddNode(graphId, assistantNode);
        await emit(new TurnCompletedServerEvent { Node = assistantNode });
    }

    // The model's function-call arguments (object?-valued) → JsonElement, the shape
    // McpHost.CallAsync forwards to the server.
    private static Dictionary<string, JsonElement> ToArgsDict(IReadOnlyDictionary<string, object?> args)
    {
        var d = new Dictionary<string, JsonElement>(args.Count);
        foreach (var (k, v) in args)
            d[k] = JsonSerializer.SerializeToElement(v);
        return d;
    }

    private static string TruncateText(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // A graph title from the first user message: first non-empty line, collapsed
    // whitespace, truncated. Editable inline afterwards.
    private static string DeriveTitle(string text)
    {
        var firstLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        var collapsed = System.Text.RegularExpressions.Regex.Replace(firstLine, @"\s+", " ");
        return string.IsNullOrEmpty(collapsed) ? "New conversation" : TruncateText(collapsed, 48);
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

    // Collect all ancestors of `nodeId` following BOTH parentId and mergeParents,
    // deduplicated by id, ordered by createdAt. For a tree node this is the simple
    // ancestor chain; for a P2 merge node (multiple parents) it's the deduplicated
    // union of every parent's ancestor path (SPEC.md §4.4). User turns are sent
    // as-is; assistant turns use their stored raw text (not a re-render).
    public static List<(string Role, string Content)> BuildHistory(Graph graph, string nodeId) =>
        BuildHistory(graph, new[] { nodeId });

    // Union of the ancestor paths of every node in `nodeIds` (dedup, chronological).
    // For one id this is the simple ancestor chain; for several (a synthesis over
    // selected branches) it is the deduplicated union of all their explorations.
    public static List<(string Role, string Content)> BuildHistory(Graph graph, IReadOnlyList<string> nodeIds)
    {
        var byId = graph.Nodes.ToDictionary(n => n.Id);
        var seen = new HashSet<string>();
        var collected = new List<Node>();
        var stack = new Stack<string>();
        foreach (var id in nodeIds)
            stack.Push(id);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!seen.Add(id) || !byId.TryGetValue(id, out var node))
                continue;
            collected.Add(node);
            if (node.ParentId is not null)
                stack.Push(node.ParentId);
            if (node.MergeParents is not null)
                foreach (var p in node.MergeParents)
                    stack.Push(p);
        }

        collected.Sort((a, b) => string.CompareOrdinal(a.CreatedAt, b.CreatedAt));
        return collected.Select(n => (n.Role, n.Raw)).ToList();
    }
}
