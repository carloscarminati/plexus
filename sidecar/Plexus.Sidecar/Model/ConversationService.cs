using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Client;
using Plexus.Sidecar.Contract;
using Plexus.Sidecar.Mcp;
using Plexus.Sidecar.Model;
using Plexus.Sidecar.Persistence;
using Plexus.Sidecar.Routing;
using Plexus.Sidecar.Services;
using AnthropicTool = Anthropic.Models.Messages.Tool;

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
    private readonly AnthropicTurnService _turns;
    private readonly LinkCardResolver _linkCards;
    private readonly IModelRouter _router;
    private readonly ModelRegistry _registry;
    private readonly ITelemetrySink _telemetry;
    private readonly Mcp.McpHost _mcp;

    public ConversationService(
        GraphStore store,
        AnthropicTurnService turns,
        LinkCardResolver linkCards,
        IModelRouter router,
        ModelRegistry registry,
        ITelemetrySink telemetry,
        Mcp.McpHost mcp)
    {
        _store = store;
        _turns = turns;
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
            graph, graphId, contextNodeId: userNode.Id, assistantParentId: userNode.Id,
            stickyFromNodeId: primary, requestedPolicy, emit, confirmTool, ct);
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
            graph, graphId, contextNodeId: userNode.Id, assistantParentId: userNode.Id,
            stickyFromNodeId: userNode.ParentId, policy, emit, confirmTool, ct);
    }

    // Shared model-call + persist path for both a normal turn and an Escalate
    // sibling. Reconstructs context from `contextNodeId`, picks a model, runs the
    // (gated) tool loop, records telemetry, and persists the assistant node under
    // `assistantParentId`. `stickyFromNodeId` drives branch stickiness (§4.1).
    private async Task GenerateAssistantAsync(
        Graph graph,
        string graphId,
        string contextNodeId,
        string assistantParentId,
        string? stickyFromNodeId,
        RoutingPolicy? requestedPolicy,
        Func<ServerEvent, Task> emit,
        Func<ToolConfirmRequest, Task<bool>>? confirmTool,
        CancellationToken ct)
    {
        // 2. Reserve the assistant node id and tell the UI a turn is underway.
        var assistantId = Guid.NewGuid().ToString("n");
        await emit(new TurnStartedServerEvent { NodeId = assistantId, ParentId = assistantParentId });

        // 3. Reconstruct context: ancestors of the context node, oldest first.
        var history = BuildHistory(graph, contextNodeId);

        // 3b. MCP tools (M0): expose discovered tools to the model under a unique
        //     "{server}_{tool}" name. If any tool is available the turn requires a
        //     tool-capable model (drives R1's capability filter).
        var toolMap = new Dictionary<string, McpToolRef>();
        var anthropicTools = new List<AnthropicTool>();
        foreach (var t in _mcp.Tools)
        {
            var exposed = ExposedToolName(t, toolMap.Keys);
            toolMap[exposed] = t;
            anthropicTools.Add(ToAnthropicTool(exposed, t.Tool));
        }

        // 4. Resolve the effective policy and pick a model.
        var requires = new RequestRequirements(
            ToolCall: anthropicTools.Count > 0,
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

        async Task<string> ExecuteTool(string toolUseId, string toolName, JsonElement args, CancellationToken c)
        {
            if (!toolMap.TryGetValue(toolName, out var t))
                return $"[error] unknown tool '{toolName}'.";

            var readOnly = t.ReadOnly;
            // Conservative floor: confirm anything not explicitly read-only.
            // `confirm-all` tightens to confirm everything; no policy loosens below this.
            var needConfirm = string.Equals(t.ServerPolicy, "confirm-all", StringComparison.OrdinalIgnoreCase) || !readOnly;

            var approved = true;
            if (needConfirm)
                approved = confirmTool is not null &&
                    await confirmTool(new ToolConfirmRequest(assistantId, toolUseId, t.ServerId, t.ServerName, t.Tool.Name, args, readOnly));

            if (!approved)
            {
                toolCalls.Add(new ToolCallRecord { ServerId = t.ServerId, Tool = t.Tool.Name, Args = args, ResultSummary = "(denied by the user)", ReadOnly = readOnly, Approved = false });
                return "The user denied this tool call. Do not retry it; continue without it.";
            }

            var resultText = await _mcp.CallAsync(t.ServerId, t.Tool.Name, ToArgsDict(args), c);
            toolCalls.Add(new ToolCallRecord { ServerId = t.ServerId, Tool = t.Tool.Name, Args = args, ResultSummary = TruncateText(resultText, 400), ReadOnly = readOnly, Approved = true });
            return resultText;
        }

        IReadOnlyList<AnthropicTool>? toolList = anthropicTools.Count > 0 ? anthropicTools : null;
        AnthropicTurnService.ToolExecutor? executor = null;
        if (anthropicTools.Count > 0)
            executor = ExecuteTool;

        var sw = Stopwatch.StartNew();
        var result = await _turns.CompleteAsync(new TurnRequest(history), choice.ModelId, toolList, executor, ct);
        sw.Stop();

        // 6. Resolve OG images for any link cards (server-side, spec P0).
        await _linkCards.EnrichAsync(result.Blocks, ct);

        // 7. Cost + telemetry (real policy/reason; telemetry schema unchanged).
        var cost = _registry.EstimateCostUsd(choice.ProviderId, choice.ModelId, result.TokensIn, result.TokensOut);
        _telemetry.Record(new TelemetryRecord(
            DateTimeOffset.UtcNow.ToString("o"), graphId, assistantId,
            choice.ModelId, choice.ProviderId, result.TokensIn, result.TokensOut,
            cost, sw.ElapsedMilliseconds, canonical, choice.Reason));

        // 8. Persist and emit the assistant node under its parent (the user node;
        //    for an Escalate this is the same parent as the original → a sibling).
        var assistantNode = new Node
        {
            Id = assistantId,
            ParentId = assistantParentId,
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

    private static readonly char[] _exposedTrim = { '_' };

    // Unique, model-safe tool name: "{server}_{tool}" sanitized to [A-Za-z0-9_], ≤64 chars.
    private static string ExposedToolName(McpToolRef t, IEnumerable<string> taken)
    {
        string Sani(string s) => new string(s.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        var baseName = (Sani(t.ServerId) + "_" + Sani(t.Tool.Name)).Trim(_exposedTrim);
        if (baseName.Length > 64) baseName = baseName[..64];
        var set = new HashSet<string>(taken);
        var name = baseName;
        for (var i = 1; set.Contains(name); i++)
            name = $"{baseName[..Math.Min(baseName.Length, 60)]}_{i}";
        return name;
    }

    private static AnthropicTool ToAnthropicTool(string name, McpClientTool t)
    {
        var props = new Dictionary<string, JsonElement>();
        List<string>? required = null;
        var schema = t.JsonSchema;
        if (schema.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object)
                foreach (var prop in p.EnumerateObject())
                    props[prop.Name] = prop.Value;
            if (schema.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array)
                required = r.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
        }
        return new AnthropicTool
        {
            Name = name,
            Description = string.IsNullOrEmpty(t.Description) ? t.Name : t.Description,
            InputSchema = new() { Properties = props, Required = required },
        };
    }

    private static Dictionary<string, JsonElement> ToArgsDict(JsonElement args)
    {
        var d = new Dictionary<string, JsonElement>();
        if (args.ValueKind == JsonValueKind.Object)
            foreach (var p in args.EnumerateObject())
                d[p.Name] = p.Value;
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
    public static List<(string Role, string Content)> BuildHistory(Graph graph, string nodeId)
    {
        var byId = graph.Nodes.ToDictionary(n => n.Id);
        var seen = new HashSet<string>();
        var collected = new List<Node>();
        var stack = new Stack<string>();
        stack.Push(nodeId);
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
