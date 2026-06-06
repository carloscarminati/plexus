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
import { formatCost, shortModel } from "./format";

const NODE_W = 240;
const NODE_H = 96;

interface CardData extends Record<string, unknown> {
  role: string;
  preview: string;
  types: string[];
  badge?: string; // model + cost, for assistant nodes
  reason?: string;
  thinking?: boolean;
  selected?: boolean;
}

function NodeCard({ data }: NodeProps<Node<CardData>>) {
  return (
    <div className={`canvas-card card-${data.role} ${data.selected ? "card-selected" : ""}`}>
      <Handle type="target" position={Position.Top} />
      <div className="card-head">
        <span className="card-role">{data.role}</span>
        {data.badge && (
          <span className="card-badge" title={data.reason}>
            {data.badge}
          </span>
        )}
      </div>
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

function badgeOf(meta: { model?: string; costUsd?: number } | undefined): string | undefined {
  if (!meta?.model) return undefined;
  const cost = meta.costUsd != null ? ` · ${formatCost(meta.costUsd)}` : "";
  return `${shortModel(meta.model)}${cost}`;
}

export function CanvasView({
  graph,
  selectedIds,
  pending,
  onClickNode,
}: {
  graph: Graph;
  selectedIds: string[];
  pending: Pending | null;
  onClickNode: (id: string, additive: boolean) => void;
}) {
  const selected = new Set(selectedIds);
  const { nodes, edges } = useMemo(() => {
    const g = new Dagre.graphlib.Graph().setDefaultEdgeLabel(() => ({}));
    g.setGraph({ rankdir: "TB", nodesep: 28, ranksep: 56 });

    const cards = graph.nodes.map((n) => ({
      id: n.id,
      role: n.role,
      preview: previewOf(n.blocks),
      types: n.blocks.map((b) => b.type),
      parentId: n.parentId,
      mergeParents: n.mergeParents ?? [],
      badge: badgeOf(n.meta),
      reason: n.meta?.reason,
    }));
    // Live "thinking" node for the in-flight turn.
    if (pending && !graph.nodes.some((n) => n.id === pending.nodeId)) {
      cards.push({
        id: pending.nodeId,
        role: "assistant",
        preview: "",
        types: [],
        parentId: pending.parentId,
        mergeParents: [],
        badge: undefined,
        reason: undefined,
      });
    }

    const ids = new Set(cards.map((c) => c.id));
    for (const c of cards) g.setNode(c.id, { width: NODE_W, height: NODE_H });
    const rfEdges: Edge[] = [];
    for (const c of cards) {
      const parents: { id: string; merge: boolean }[] = [];
      if (c.parentId) parents.push({ id: c.parentId, merge: false });
      for (const p of c.mergeParents) parents.push({ id: p, merge: true });
      for (const p of parents) {
        if (!ids.has(p.id)) continue;
        g.setEdge(p.id, c.id);
        rfEdges.push({
          id: `${p.id}-${c.id}`,
          source: p.id,
          target: c.id,
          // P2 merge edges are dashed + animated to read as a union, not a tree edge.
          animated: p.merge,
          style: p.merge ? { strokeDasharray: "5 4", stroke: "var(--accent)" } : undefined,
        });
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
          badge: c.badge,
          reason: c.reason,
          thinking: pending?.nodeId === c.id,
          selected: selected.has(c.id),
        },
        width: NODE_W,
        height: NODE_H,
      };
    });

    return { nodes: rfNodes, edges: rfEdges };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [graph, selectedIds.join(","), pending]);

  return (
    <ReactFlow
      nodes={nodes}
      edges={edges}
      nodeTypes={nodeTypes}
      onNodeClick={(e, node) => onClickNode(node.id, e.shiftKey || e.metaKey)}
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
