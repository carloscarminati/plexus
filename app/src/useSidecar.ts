import { useCallback, useEffect, useRef, useState } from "react";
import type { AdjudicationDecision, AppSettingsView, ClientEvent, Graph, McpServerView, ModelInfo, ProviderView, RoutingPolicy, ServerEvent } from "./contract";
import { emptyReasoningSession, type ReasoningSession } from "./reasoning-view";

const SIDECAR_URL = "ws://127.0.0.1:8765/ws";

export type Status = "connecting" | "online" | "offline";

export interface Pending {
  nodeId: string;
  parentId: string | null;
}

// One entry in the graph history sidebar.
export type GraphSummary = Extract<ServerEvent, { type: "graphs" }>["graphs"][number];

export function useSidecar() {
  const [status, setStatus] = useState<Status>("connecting");
  const [graph, setGraph] = useState<Graph | null>(null);
  const [pending, setPending] = useState<Pending | null>(null);
  const [error, setError] = useState<string | null>(null);
  // Selection is a set: 1 node = branch/resume from it; ≥2 = P2 DAG merge.
  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  const [models, setModels] = useState<ModelInfo[]>([]);
  const [sessionPolicy, setSessionPolicyState] = useState<RoutingPolicy>({ kind: "manual", modelId: "claude-opus-4-8" });
  const [nodeOverrides, setNodeOverrides] = useState<Record<string, RoutingPolicy>>({});
  // M0: a pending MCP tool-confirmation request (host is waiting on the user).
  const [confirm, setConfirm] = useState<Extract<ServerEvent, { type: "tool_confirmation_request" }> | null>(null);
  // Graph history (sidebar), most-recently-active first.
  const [graphs, setGraphs] = useState<GraphSummary[]>([]);
  // Consolidated app config for the Settings panel (null until first snapshot).
  const [settings, setSettings] = useState<AppSettingsView | null>(null);
  // ADR-0002 Rx (dev): the reasoning-recipe run + loaded argument graph.
  const [reasoning, setReasoning] = useState<ReasoningSession>(emptyReasoningSession);
  const socketRef = useRef<WebSocket | null>(null);
  // Latest active graph id, readable inside the socket handler without stale closures.
  const activeIdRef = useRef<string | null>(null);
  // Auto-open (last active / new) runs once, on the first graph list.
  const initializedRef = useRef(false);

  const send = useCallback((event: ClientEvent) => {
    const sock = socketRef.current;
    if (sock?.readyState === WebSocket.OPEN) sock.send(JSON.stringify(event));
  }, []);

  useEffect(() => {
    let closed = false;
    let retry: ReturnType<typeof setTimeout> | undefined;

    const handle = (msg: ServerEvent) => {
      switch (msg.type) {
        case "graph":
          // Authoritatively mark the active graph NOW (not via the post-render
          // effect) so a `graphs` refresh arriving right after — e.g. when the
          // server pruned the empty conversation we just left — doesn't see a stale
          // active id and bounce us to a different conversation.
          activeIdRef.current = msg.graph.id;
          setGraph(msg.graph);
          setPending(null);
          {
            const last = msg.graph.nodes[msg.graph.nodes.length - 1]?.id;
            setSelectedIds(last ? [last] : []);
          }
          if (msg.graph.defaultPolicy) setSessionPolicyState(msg.graph.defaultPolicy);
          break;
        case "node_created":
          setGraph((g) => (g ? { ...g, nodes: [...g.nodes, msg.node] } : g));
          break;
        case "turn_started":
          setPending({ nodeId: msg.nodeId, parentId: msg.parentId });
          break;
        case "turn_completed":
          setGraph((g) => (g ? { ...g, nodes: [...g.nodes, msg.node] } : g));
          setPending(null);
          setSelectedIds([msg.node.id]); // continue from the fresh assistant node
          break;
        case "models":
          setModels(msg.models);
          break;
        case "graphs": {
          setGraphs(msg.graphs);
          // Keep the active graph's title in sync (server may derive/rename it).
          setGraph((g) => {
            if (!g) return g;
            const s = msg.graphs.find((x) => x.id === g.id);
            return s ? { ...g, title: s.title } : g;
          });
          const activeId = activeIdRef.current;
          const activeExists = !!activeId && msg.graphs.some((x) => x.id === activeId);
          // First list → open the last active graph (or create one). Later, if the
          // active graph just got deleted, fall back to the most recent / a new one.
          if (!initializedRef.current) {
            initializedRef.current = true;
            send(msg.graphs.length > 0 ? { type: "load_graph", graphId: msg.graphs[0].id } : { type: "new_graph" });
          } else if (activeId && !activeExists) {
            send(msg.graphs.length > 0 ? { type: "load_graph", graphId: msg.graphs[0].id } : { type: "new_graph" });
          }
          break;
        }
        case "settings":
          setSettings({
            confirmTimeoutSeconds: msg.confirmTimeoutSeconds,
            defaultPolicy: msg.defaultPolicy,
            anthropicKeyConfigured: msg.anthropicKeyConfigured,
            mcpServers: msg.mcpServers,
            providers: msg.providers ?? [],
          });
          break;
        case "tool_confirmation_request":
          setConfirm(msg);
          break;
        case "error":
          setError(msg.message);
          setPending(null);
          setConfirm(null); // a cancelled/timed-out turn clears any open confirmation
          setReasoning((r) => (r.status === "running" || r.status === "loading" ? { ...r, status: "error", error: msg.message } : r));
          break;
        // ADR-0002 Rx (dev): recipe run finished → fetch its graph + R1; then render.
        case "recipe_run_done":
          send({ type: "load_reasoning_graph", graphId: msg.graphId });
          setReasoning((r) => ({ ...r, status: "loading" }));
          break;
        case "reasoning_graph":
          setReasoning({ status: "ready", graph: msg.graph, diagnostics: msg.diagnostics, openUncertainties: msg.openUncertainties, adjudication: msg.adjudication ?? null, hypothesisNets: msg.hypothesisNets });
          break;
        // ADR-0002 Rx.2.0: a recorded adjudication — merged beside the unchanged view.
        case "adjudication_saved":
          setReasoning((r) => ({ ...r, adjudication: msg.adjudication }));
          break;
      }
    };

    const connect = () => {
      setStatus("connecting");
      const sock = new WebSocket(SIDECAR_URL);
      socketRef.current = sock;
      sock.onopen = () => {
        setStatus("online");
        sock.send(JSON.stringify({ type: "list_models" } satisfies ClientEvent));
        // The graph list drives startup: open the last active one, or create one.
        sock.send(JSON.stringify({ type: "list_graphs" } satisfies ClientEvent));
        sock.send(JSON.stringify({ type: "get_settings" } satisfies ClientEvent));
      };
      sock.onmessage = (ev) => {
        try {
          handle(JSON.parse(ev.data) as ServerEvent);
        } catch {
          /* ignore malformed frame */
        }
      };
      sock.onclose = () => {
        setStatus("offline");
        if (!closed) retry = setTimeout(connect, 1500);
      };
      sock.onerror = () => sock.close();
    };

    connect();
    return () => {
      closed = true;
      if (retry) clearTimeout(retry);
      socketRef.current?.close();
    };
  }, []);

  // Keep the ref the socket handler reads in sync with the active graph.
  useEffect(() => {
    activeIdRef.current = graph?.id ?? null;
  }, [graph]);

  // ── Graph management (new / open / rename / delete) ───────────────────────
  const newGraph = useCallback(() => {
    send({ type: "new_graph" }); // title is derived from the first message
  }, [send]);

  const openGraph = useCallback(
    (id: string) => {
      if (id === activeIdRef.current) return;
      setError(null);
      send({ type: "load_graph", graphId: id });
    },
    [send],
  );

  const renameGraph = useCallback(
    (id: string, title: string) => {
      const t = title.trim() || undefined;
      send({ type: "set_graph_title", graphId: id, title: t });
      setGraphs((gs) => gs.map((g) => (g.id === id ? { ...g, title: t } : g))); // optimistic
      setGraph((g) => (g && g.id === id ? { ...g, title: t } : g));
    },
    [send],
  );

  const deleteGraph = useCallback(
    (id: string) => {
      send({ type: "delete_graph", graphId: id });
      setGraphs((gs) => gs.filter((g) => g.id !== id)); // optimistic; server confirms + may switch
    },
    [send],
  );

  const pinGraph = useCallback(
    (id: string, pinned: boolean) => {
      send({ type: "set_graph_pinned", graphId: id, pinned });
      setGraphs((gs) => gs.map((g) => (g.id === id ? { ...g, pinned } : g))); // optimistic
    },
    [send],
  );

  // ── Settings (all persist server-side; the panel re-renders from the snapshot) ──
  const setGeneralSettings = useCallback(
    (confirmTimeoutSeconds: number) => send({ type: "set_general_settings", confirmTimeoutSeconds }),
    [send],
  );
  const setDefaultPolicy = useCallback(
    (policy: RoutingPolicy) => send({ type: "set_default_policy", policy }),
    [send],
  );
  const setAnthropicKey = useCallback((key: string) => send({ type: "set_anthropic_key", key }), [send]);
  const deleteAnthropicKey = useCallback(() => send({ type: "delete_anthropic_key" }), [send]);
  const setMcpServer = useCallback(
    (server: McpServerView, httpCredential?: string) => send({ type: "set_mcp_server", server, httpCredential }),
    [send],
  );
  const deleteMcpServer = useCallback((id: string) => send({ type: "delete_mcp_server", id }), [send]);
  const setProvider = useCallback(
    (provider: ProviderView, apiKey?: string) => send({ type: "set_provider", provider, apiKey }),
    [send],
  );
  const deleteProvider = useCallback((id: string) => send({ type: "delete_provider", id }), [send]);
  // ADR-0002 Rx (dev): trigger a reasoning-recipe run over raw case text.
  const runReasoning = useCallback((caseText: string) => {
    setReasoning({ ...emptyReasoningSession, status: "running" });
    send({ type: "dev_run_recipe", caseText });
  }, [send]);
  // ADR-0002 Rx.2.0: record/update the human adjudication on the loaded graph. Additive —
  // the server confirms with adjudication_saved; the argument view is untouched.
  const adjudicate = useCallback((graphId: string, decision: AdjudicationDecision, note?: string) => {
    send({ type: "adjudicate_graph", graphId, decision, note: note?.trim() ? note.trim() : undefined });
  }, [send]);

  // Click a node: plain click selects only it; shift/cmd-click toggles it in the
  // set (for a DAG merge of ≥2 nodes).
  const clickNode = useCallback((id: string, additive: boolean) => {
    setSelectedIds((prev) => {
      if (!additive) return [id];
      return prev.includes(id) ? prev.filter((x) => x !== id) : [...prev, id];
    });
  }, []);

  const primaryId = selectedIds.length > 0 ? selectedIds[0] : null;

  // The policy for a turn from `nodeId`: the node's override if set, else the
  // session default. Manual override wins.
  const effectivePolicy = useCallback(
    (nodeId: string | null): RoutingPolicy => (nodeId && nodeOverrides[nodeId]) || sessionPolicy,
    [nodeOverrides, sessionPolicy],
  );

  const sendMessage = useCallback(
    (text: string) => {
      if (!graph || !text.trim() || pending) return;
      setError(null);
      const merge = selectedIds.length >= 2;
      send({
        type: "send_message",
        graphId: graph.id,
        fromNodeId: primaryId,
        fromNodeIds: merge ? selectedIds : undefined,
        text,
        policy: effectivePolicy(primaryId),
      });
    },
    [graph, selectedIds, primaryId, pending, send, effectivePolicy],
  );

  const sendChoice = useCallback(
    (nodeId: string, option: { id: string; label: string }) => {
      if (!graph || pending) return;
      setError(null);
      send({ type: "intent", graphId: graph.id, nodeId, kind: "choice", payload: option, policy: effectivePolicy(nodeId) });
    },
    [graph, pending, send, effectivePolicy],
  );

  const setSessionPolicy = useCallback(
    (policy: RoutingPolicy) => {
      setSessionPolicyState(policy);
      if (graph) send({ type: "set_session_policy", graphId: graph.id, policy });
    },
    [graph, send],
  );

  const respondConfirm = useCallback(
    (approved: boolean) => {
      setConfirm((c) => {
        if (c) send({ type: "tool_confirmation", toolUseId: c.toolUseId, approved });
        return null;
      });
    },
    [send],
  );

  // R1 §4.2 — re-run the input that produced an assistant node with a (usually
  // stronger) model as a sibling branch. Default (no policy) = Auto-quality.
  const escalate = useCallback(
    (nodeId: string, policy?: RoutingPolicy) => {
      if (!graph || pending) return;
      setError(null);
      send({ type: "escalate", graphId: graph.id, nodeId, policy });
    },
    [graph, pending, send],
  );

  // X1 — converge the selected branches into a decision-brief deliverable node.
  const synthesize = useCallback(
    (fromNodeIds: string[], policy?: RoutingPolicy) => {
      if (!graph || pending || fromNodeIds.length === 0) return;
      setError(null);
      send({ type: "synthesize", graphId: graph.id, fromNodeIds, policy });
    },
    [graph, pending, send],
  );

  const setNodeOverride = useCallback((nodeId: string, policy: RoutingPolicy | null) => {
    setNodeOverrides((prev) => {
      const next = { ...prev };
      if (policy) next[nodeId] = policy;
      else delete next[nodeId];
      return next;
    });
  }, []);

  return {
    status,
    graph,
    graphs,
    newGraph,
    openGraph,
    renameGraph,
    deleteGraph,
    pinGraph,
    pending,
    error,
    selectedIds,
    selectedId: primaryId, // the node shown in the detail pane
    clickNode,
    models,
    sessionPolicy,
    setSessionPolicy,
    nodeOverrides,
    setNodeOverride,
    confirm,
    respondConfirm,
    sendMessage,
    sendChoice,
    escalate,
    synthesize,
    settings,
    setGeneralSettings,
    setDefaultPolicy,
    setAnthropicKey,
    deleteAnthropicKey,
    setMcpServer,
    deleteMcpServer,
    setProvider,
    deleteProvider,
    reasoning,
    runReasoning,
    adjudicate,
  };
}
