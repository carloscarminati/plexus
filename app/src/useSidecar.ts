import { useCallback, useEffect, useRef, useState } from "react";
import type { ClientEvent, Graph, ServerEvent } from "./contract";

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
  // The node a new message branches from. null = start a fresh root turn.
  const [selectedId, setSelectedId] = useState<string | null>(null);
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
          setSelectedId(msg.node.id); // continue from the fresh assistant node
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

  // Also append the user node's edge as it arrives, for live canvas wiring.
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

  const sendMessage = useCallback(
    (text: string) => {
      if (!graph || !text.trim() || pending) return;
      setError(null);
      send({ type: "send_message", graphId: graph.id, fromNodeId: selectedId, text });
    },
    [graph, selectedId, pending, send],
  );

  const sendChoice = useCallback(
    (nodeId: string, option: { id: string; label: string }) => {
      if (!graph || pending) return;
      setError(null);
      send({ type: "intent", graphId: graph.id, nodeId, kind: "choice", payload: option });
    },
    [graph, pending, send],
  );

  return { status, graph, pending, error, selectedId, select: setSelectedId, sendMessage, sendChoice };
}
