import { useState, type KeyboardEvent } from "react";
import type { GraphSummary } from "./useSidecar";
import { timeAgo } from "./format";

// Graph history: new conversation, list (most-recent first), open, and a per-row
// overflow (kebab) menu with Rename + Delete (so pin/etc. slot in later). State
// lives in the sidecar; this is presentation only.
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
  const [menuId, setMenuId] = useState<string | null>(null);
  const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null);

  const startEdit = (g: GraphSummary) => {
    setEditingId(g.id);
    setDraft(g.title ?? "");
    setMenuId(null);
  };
  const commitEdit = () => {
    if (editingId) onRename(editingId, draft);
    setEditingId(null);
  };
  const onEditKey = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === "Enter") commitEdit();
    if (e.key === "Escape") setEditingId(null);
  };

  const confirmTarget = graphs.find((g) => g.id === confirmDeleteId) ?? null;

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
          return (
            <div
              key={g.id}
              className={`graph-item ${active ? "active" : ""} ${menuId === g.id ? "menu-open" : ""}`}
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
                <>
                  <div className="graph-main">
                    <div className="graph-name">{g.title || "New conversation"}</div>
                    <div className="graph-time">{timeAgo(g.updatedAt)}</div>
                  </div>
                  <button
                    className="graph-kebab"
                    title="Conversation actions"
                    aria-label="Conversation actions"
                    onClick={(e) => {
                      e.stopPropagation();
                      setMenuId(menuId === g.id ? null : g.id);
                    }}
                  >
                    ⋯
                  </button>
                  {menuId === g.id && (
                    <>
                      <div className="menu-backdrop" onClick={(e) => { e.stopPropagation(); setMenuId(null); }} />
                      <div className="graph-menu" onClick={(e) => e.stopPropagation()}>
                        <button onClick={() => startEdit(g)}>Rename</button>
                        <button
                          className="graph-menu-danger"
                          onClick={() => {
                            setConfirmDeleteId(g.id);
                            setMenuId(null);
                          }}
                        >
                          Delete
                        </button>
                      </div>
                    </>
                  )}
                </>
              )}
            </div>
          );
        })}
      </div>

      {confirmTarget && (
        <div className="confirm-overlay" onClick={() => setConfirmDeleteId(null)}>
          <div className="confirm-card" onClick={(e) => e.stopPropagation()}>
            <div className="confirm-title">Delete this conversation?</div>
            <div className="confirm-sub">
              “{confirmTarget.title || "New conversation"}” — this can’t be undone.
            </div>
            <div className="confirm-actions">
              <button className="confirm-deny" onClick={() => setConfirmDeleteId(null)}>
                Cancel
              </button>
              <button
                className="confirm-delete"
                onClick={() => {
                  onDelete(confirmTarget.id);
                  setConfirmDeleteId(null);
                }}
              >
                Delete
              </button>
            </div>
          </div>
        </div>
      )}
    </aside>
  );
}
