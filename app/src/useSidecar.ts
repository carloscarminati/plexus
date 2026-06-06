import { useCallback, useEffect, useRef, useState } from "react";
import type { ClientEvent, Graph, Node, ServerEvent } from "./contract";

const SIDECAR_URL = "ws://127.0.0.1:8765/ws";

export type Status = "connecting" | "online" | "offline";

// A pending assistant turn: we know its id from `turn_started` before the
// blocks arrive, so the UI can show a "thinking" placeholder.
export interface Pending {
  nodeId: string;
  parentId: string | null;
}

export function useSidecar() {
  const [status, setStatus] = useState<Status>("connecting");
  const [graph, setGraph] = useState<Graph | null>(null);
  const [pending, setPending] = useState<Pending | null>(null);
  const [error, setError] = useState<string | null>(null);
  const socketRef = useRef<WebSocket | null>(null);

  const send = useCallback((event: ClientEvent) => {
    const sock = socketRef.current;
    if (sock?.readyState === WebSocket.OPEN) sock.send(JSON.stringify(event));
  }, []);

  useEffect(() => {
    let closed = false;
    let retry: ReturnType<typeof setTimeout> | undefined;

    const connect = () => {
      setStatus("connecting");
      const sock = new WebSocket(SIDECAR_URL);
      socketRef.current = sock;

      sock.onopen = () => {
        setStatus("online");
        sock.send(JSON.stringify({ type: "new_graph", title: "Conversation" } satisfies ClientEvent));
      };

      sock.onmessage = (ev) => {
        let msg: ServerEvent;
        try {
          msg = JSON.parse(ev.data);
        } catch {
          return;
        }
        handle(msg);
      };

      sock.onclose = () => {
        setStatus("offline");
        if (!closed) retry = setTimeout(connect, 1500);
      };
      sock.onerror = () => sock.close();
    };

    const handle = (msg: ServerEvent) => {
      switch (msg.type) {
        case "graph":
          setGraph(msg.graph);
          setPending(null);
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
          break;
        case "error":
          setError(msg.message);
          setPending(null);
          break;
      }
    };

    connect();
    return () => {
      closed = true;
      if (retry) clearTimeout(retry);
      socketRef.current?.close();
    };
  }, []);

  const sendMessage = useCallback(
    (text: string) => {
      if (!graph || !text.trim()) return;
      setError(null);
      // Linear P0: branch from the last node (resume-from-node generalizes this).
      const last: Node | undefined = graph.nodes[graph.nodes.length - 1];
      send({ type: "send_message", graphId: graph.id, fromNodeId: last?.id ?? null, text });
    },
    [graph, send],
  );

  return { status, graph, pending, error, sendMessage };
}
