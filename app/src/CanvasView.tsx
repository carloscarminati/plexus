import { useMemo } from "react";
import Dagre from "@dagrejs/dagre";
import {
  ReactFlow,
  Background,
  Controls,
  Handle,
  Position,
  type Edge,
  type Node,
  type NodeProps,
} from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import type { Block, Graph } from "./contract";
import type { Pending } from "./useSidecar";

const NODE_W = 240;
const NODE_H = 96;

interface CardData extends Record<string, unknown> {
  role: string;
  preview: string;
  types: string[];
  thinking?: boolean;
  selected?: boolean;
}

function NodeCard({ data }: NodeProps<Node<CardData>>) {
  return (
    <div className={`canvas-card card-${data.role} ${data.selected ? "card-selected" : ""}`}>
      <Handle type="target" position={Position.Top} />
      <div className="card-role">{data.role}</div>
      {data.thinking ? (
        <div className="thinking">
          <span></span>
          <span></span>
          <span></span>
        </div>
      ) : (
        <>
          <div className="card-preview">{data.preview}</div>
          <div className="card-types">
            {data.types.map((t, i) => (
              <span key={i} className="card-chip">
                {t}
              </span>
            ))}
          </div>
        </>
      )}
      <Handle type="source" position={Position.Bottom} />
    </div>
  );
}

const nodeTypes = { plexus: NodeCard };

function previewOf(blocks: Block[]): string {
  const first = blocks[0];
  if (!first) return "";
  switch (first.type) {
    case "markdown":
      return first.text.replace(/[#*`>\-]/g, "").trim().slice(0, 80);
    case "table":
      return first.caption ?? `table · ${first.columns.length} cols`;
    case "code":
      return first.filename ?? `${first.language} code`;
    case "link_card":
      return first.title ?? first.url;
    case "chart":
      return `${first.chart} chart`;
    case "choices":
      return first.prompt ?? "choices";
    default:
      return first.type;
  }
}

export function CanvasView({
  graph,
  selectedId,
  pending,
  onSelect,
}: {
  graph: Graph;
  selectedId: string | null;
  pending: Pending | null;
  onSelect: (id: string) => void;
}) {
  const { nodes, edges } = useMemo(() => {
    const g = new Dagre.graphlib.Graph().setDefaultEdgeLabel(() => ({}));
    g.setGraph({ rankdir: "TB", nodesep: 28, ranksep: 56 });

    const cards = graph.nodes.map((n) => ({
      id: n.id,
      role: n.role,
      preview: previewOf(n.blocks),
      types: n.blocks.map((b) => b.type),
      parentId: n.parentId,
    }));
    // Live "thinking" node for the in-flight turn.
    if (pending && !graph.nodes.some((n) => n.id === pending.nodeId)) {
      cards.push({
        id: pending.nodeId,
        role: "assistant",
        preview: "",
        types: [],
        parentId: pending.parentId,
      });
    }

    for (const c of cards) g.setNode(c.id, { width: NODE_W, height: NODE_H });
    const rfEdges: Edge[] = [];
    for (const c of cards) {
      if (c.parentId && cards.some((x) => x.id === c.parentId)) {
        g.setEdge(c.parentId, c.id);
        rfEdges.push({ id: `${c.parentId}-${c.id}`, source: c.parentId, target: c.id });
      }
    }
    Dagre.layout(g);

    const rfNodes: Node<CardData>[] = cards.map((c) => {
      const p = g.node(c.id);
      return {
        id: c.id,
        type: "plexus",
        position: { x: p.x - NODE_W / 2, y: p.y - NODE_H / 2 },
        data: {
          role: c.role,
          preview: c.preview,
          types: c.types,
          thinking: pending?.nodeId === c.id,
          selected: selectedId === c.id,
        },
        width: NODE_W,
        height: NODE_H,
      };
    });

    return { nodes: rfNodes, edges: rfEdges };
  }, [graph, selectedId, pending]);

  return (
    <ReactFlow
      nodes={nodes}
      edges={edges}
      nodeTypes={nodeTypes}
      onNodeClick={(_, node) => onSelect(node.id)}
      nodesDraggable={false}
      fitView
      fitViewOptions={{ padding: 0.25, maxZoom: 1 }}
      proOptions={{ hideAttribution: true }}
      minZoom={0.2}
    >
      <Background gap={20} color="#222838" />
      <Controls showInteractive={false} />
    </ReactFlow>
  );
}
