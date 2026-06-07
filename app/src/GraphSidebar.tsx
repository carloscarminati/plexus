import { useState, type KeyboardEvent } from "react";
import type { GraphSummary } from "./useSidecar";
import { timeAgo } from "./format";

// Graph history: new conversation, list (most-recent first), open, inline rename,
// delete (with confirm). State lives in the sidecar; this is presentation only.
export function GraphSidebar({
  graphs,
  activeId,
  onNew,
  onOpen,
  onRename,
  onDelete,
}: {
  graphs: GraphSummary[];
  activeId: string | null;
  onNew: () => void;
  onOpen: (id: string) => void;
  onRename: (id: string, title: string) => void;
  onDelete: (id: string) => void;
}) {
  const [editingId, setEditingId] = useState<string | null>(null);
  const [draft, setDraft] = useState("");
  const [confirmingId, setConfirmingId] = useState<string | null>(null);

  const startEdit = (g: GraphSummary) => {
    setEditingId(g.id);
    setDraft(g.title ?? "");
  };
  const commitEdit = () => {
    if (editingId) onRename(editingId, draft);
    setEditingId(null);
  };
  const onEditKey = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === "Enter") commitEdit();
    if (e.key === "Escape") setEditingId(null);
  };

  return (
    <aside className="graphs-sidebar">
      <div className="graphs-head">
        <span className="graphs-title">Conversations</span>
        <button className="graphs-new" title="New conversation" onClick={onNew}>
          +
        </button>
      </div>
      <div className="graphs-list">
        {graphs.length === 0 && <div className="graphs-empty">No conversations yet.</div>}
        {graphs.map((g) => {
          const active = g.id === activeId;
          const editing = g.id === editingId;
          const confirming = g.id === confirmingId;
          return (
            <div
              key={g.id}
              className={`graph-item ${active ? "active" : ""}`}
              onClick={() => !editing && onOpen(g.id)}
            >
              {editing ? (
                <input
                  className="graph-rename"
                  autoFocus
                  value={draft}
                  onChange={(e) => setDraft(e.currentTarget.value)}
                  onKeyDown={onEditKey}
                  onBlur={commitEdit}
                  onClick={(e) => e.stopPropagation()}
                />
              ) : (
                <div className="graph-main">
                  <div
                    className="graph-name"
                    title="Double-click to rename"
                    onDoubleClick={(e) => {
                      e.stopPropagation();
                      startEdit(g);
                    }}
                  >
                    {g.title || "New conversation"}
                  </div>
                  <div className="graph-time">{timeAgo(g.updatedAt)}</div>
                </div>
              )}

              {!editing &&
                (confirming ? (
                  <span className="graph-confirm" onClick={(e) => e.stopPropagation()}>
                    <button
                      className="graph-confirm-yes"
                      title="Delete permanently"
                      onClick={() => {
                        onDelete(g.id);
                        setConfirmingId(null);
                      }}
                    >
                      delete
                    </button>
                    <button className="graph-confirm-no" onClick={() => setConfirmingId(null)}>
                      ✕
                    </button>
                  </span>
                ) : (
                  <button
                    className="graph-del"
                    title="Delete conversation"
                    onClick={(e) => {
                      e.stopPropagation();
                      setConfirmingId(g.id);
                    }}
                  >
                    ✕
                  </button>
                ))}
            </div>
          );
        })}
      </div>
    </aside>
  );
}
