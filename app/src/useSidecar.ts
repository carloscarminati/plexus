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
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [models, setModels] = useState<ModelInfo[]>([]);
  // R1 routing: session default policy + per-node overrides (manual always wins).
  const [sessionPolicy, setSessionPolicyState] = useState<RoutingPolicy>({ kind: "manual", modelId: "claude-opus-4-8" });
  const [nodeOverrides, setNodeOverrides] = useState<Record<string, RoutingPolicy>>({});
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
          setSelectedId(msg.graph.nodes[msg.graph.nodes.length - 1]?.id ?? null);
          if (msg.graph.defaultPolicy) setSessionPolicyState(msg.graph.defaultPolicy);
          break;
        case "node_created":
          setGraph((g) => (g ? { ...g, nodes: [...g.nodes, msg.node] } : g));
          break;
        case "turn_started":
          setPending({ nodeId: msg.nodeId, parentId: msg.parentId });
          break;
        case "turn_completed":
          setGraph((g) =>
            g
              ? {
                  ...g,
                  nodes: [...g.nodes, msg.node],
                  edges: msg.node.parentId
                    ? [...g.edges, { from: msg.node.parentId, to: msg.node.id }]
                    : g.edges,
                }
              : g,
          );
          setPending(null);
          setSelectedId(msg.node.id);
          break;
        case "models":
          setModels(msg.models);
          break;
        case "error":
          setError(msg.message);
          setPending(null);
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

  useEffect(() => {
    setGraph((g) => {
      if (!g) return g;
      const have = new Set(g.edges.map((e) => `${e.from}->${e.to}`));
      const edges = [...g.edges];
      for (const n of g.nodes) {
        if (n.parentId && !have.has(`${n.parentId}->${n.id}`)) {
          edges.push({ from: n.parentId, to: n.id });
          have.add(`${n.parentId}->${n.id}`);
        }
      }
      return edges.length === g.edges.length ? g : { ...g, edges };
    });
  }, [graph?.nodes.length]);

  // The policy that applies to a turn branching from `nodeId`: the node's
  // override if set, otherwise the session default. Manual override wins.
  const effectivePolicy = useCallback(
    (nodeId: string | null): RoutingPolicy => (nodeId && nodeOverrides[nodeId]) || sessionPolicy,
    [nodeOverrides, sessionPolicy],
  );

  const sendMessage = useCallback(
    (text: string) => {
      if (!graph || !text.trim() || pending) return;
      setError(null);
      send({ type: "send_message", graphId: graph.id, fromNodeId: selectedId, text, policy: effectivePolicy(selectedId) });
    },
    [graph, selectedId, pending, send, effectivePolicy],
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
    selectedId,
    select: setSelectedId,
    models,
    sessionPolicy,
    setSessionPolicy,
    nodeOverrides,
    setNodeOverride,
    sendMessage,
    sendChoice,
  };
}
