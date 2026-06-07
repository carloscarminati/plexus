import { useCallback, useEffect, useRef, useState } from "react";
import type { ClientEvent, Graph, ModelInfo, RoutingPolicy, ServerEvent } from "./contract";

const SIDECAR_URL = "ws://127.0.0.1:8765/ws";

export type Status = "connecting" | "online" | "offline";

export interface Pending {
  nodeId: string;
  parentId: string | null;
}

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
  const socketRef = useRef<WebSocket | null>(null);

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
        case "tool_confirmation_request":
          setConfirm(msg);
          break;
        case "error":
          setError(msg.message);
          setPending(null);
          setConfirm(null); // a cancelled/timed-out turn clears any open confirmation
          break;
      }
    };

    const connect = () => {
      setStatus("connecting");
      const sock = new WebSocket(SIDECAR_URL);
      socketRef.current = sock;
      sock.onopen = () => {
        setStatus("online");
        sock.send(JSON.stringify({ type: "new_graph", title: "Conversation" } satisfies ClientEvent));
        sock.send(JSON.stringify({ type: "list_models" } satisfies ClientEvent));
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
  };
}
