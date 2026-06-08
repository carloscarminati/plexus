import { BlockView } from "./blocks/BlockView";
import { blocksToMarkdown, saveMarkdown, deliverableFilename } from "./compose/markdown";
import type { Block } from "./contract";

// X0 COMPOSE surface — a minimal drawer (not a routed mode yet). Lists the selected
// nodes' blocks in a single ordered list (order decided by the caller) and exports
// the whole list to a Markdown file. No reordering / per-block removal / persistence
// (that's X1+); the canvas selection IS the filter.
export function ComposeDrawer({
  blocks,
  nodeCount,
  title,
  onSynthesize,
  onClose,
}: {
  blocks: Block[];
  nodeCount: number;
  title?: string;
  onSynthesize?: () => void; // X1: converge the selection into a decision brief
  onClose: () => void;
}) {
  const onExport = () => saveMarkdown(deliverableFilename(title), blocksToMarkdown(blocks));

  return (
    <div className="compose-overlay" onClick={onClose}>
      <aside className="compose-drawer" onClick={(e) => e.stopPropagation()}>
        <div className="compose-head">
          <div>
            <div className="compose-title">Compose</div>
            <div className="compose-sub">
              {nodeCount} node{nodeCount === 1 ? "" : "s"} · {blocks.length} block{blocks.length === 1 ? "" : "s"}
            </div>
          </div>
          <div className="compose-actions">
            <button
              className="btn-primary"
              disabled={nodeCount < 2 || !onSynthesize}
              title={nodeCount < 2 ? "Select 2+ branches to synthesize" : "Read the selected branches and write a decision brief"}
              onClick={onSynthesize}
            >
              ✦ Synthesize decision brief
            </button>
            <button className="btn-secondary" disabled={blocks.length === 0} onClick={onExport}>
              Export Markdown
            </button>
            <button className="settings-close" onClick={onClose} aria-label="Close">
              ✕
            </button>
          </div>
        </div>
        <div className="compose-body">
          {blocks.length === 0 ? (
            <div className="empty small">Select one or more nodes on the canvas to compose a deliverable.</div>
          ) : (
            blocks.map((block, i) => <BlockView key={i} block={block} />)
          )}
        </div>
      </aside>
    </div>
  );
}
