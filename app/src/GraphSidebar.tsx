import { useState, type KeyboardEvent } from "react";
import type { GraphSummary } from "./useSidecar";
import { timeAgo } from "./format";

// Display title — the Pass 1 rule, shared by the rows, search, and grouping.
const displayTitle = (g: GraphSummary) => g.title || "New conversation";

const GROUP_LABELS = ["Today", "Yesterday", "Previous 7 Days", "Previous 30 Days", "Older"];

// Bucket a graph by updatedAt relative to the start of today (0..4 → GROUP_LABELS).
function bucketOf(updatedAt: string | undefined, startOfToday: number): number {
  const t = updatedAt ? new Date(updatedAt).getTime() : NaN;
  if (Number.isNaN(t)) return 4;
  const day = 86400000;
  if (t >= startOfToday) return 0;
  if (t >= startOfToday - day) return 1;
  if (t >= startOfToday - 7 * day) return 2;
  if (t >= startOfToday - 30 * day) return 3;
  return 4;
}

// Graph history: new conversation, search by title, a list grouped by date (or a
// flat filtered list while searching), open, and a per-row kebab menu with Rename +
// Delete. State lives in the sidecar; this is presentation only.
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
  const [query, setQuery] = useState("");

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

  // One row — reused by both the grouped and the filtered views.
  const renderRow = (g: GraphSummary) => {
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
              <div className="graph-name">{displayTitle(g)}</div>
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
  };

  const q = query.trim().toLowerCase();
  const searching = q.length > 0;
  const filtered = searching ? graphs.filter((g) => displayTitle(g).toLowerCase().includes(q)) : graphs;

  // Grouped view buckets (only when not searching). Input is already most-recent
  // first, so within-group order stays updatedAt desc.
  const startOfToday = (() => {
    const n = new Date();
    return new Date(n.getFullYear(), n.getMonth(), n.getDate()).getTime();
  })();
  const groups: GraphSummary[][] = [[], [], [], [], []];
  if (!searching) for (const g of graphs) groups[bucketOf(g.updatedAt, startOfToday)].push(g);

  return (
    <aside className="graphs-sidebar">
      <div className="graphs-head">
        <span className="graphs-title">Conversations</span>
        <button className="graphs-new" title="New conversation" onClick={onNew}>
          +
        </button>
      </div>

      <div className="graphs-search">
        <input
          type="text"
          placeholder="Search conversations…"
          value={query}
          onChange={(e) => setQuery(e.currentTarget.value)}
        />
        {searching && (
          <button className="graphs-search-clear" aria-label="Clear search" onClick={() => setQuery("")}>
            ✕
          </button>
        )}
      </div>

      <div className="graphs-list">
        {graphs.length === 0 && <div className="graphs-empty">No conversations yet.</div>}

        {searching ? (
          filtered.length === 0 ? (
            <div className="graphs-empty">No matches.</div>
          ) : (
            filtered.map(renderRow)
          )
        ) : (
          groups.map((group, i) =>
            group.length === 0 ? null : (
              <div key={i} className="graph-group">
                <div className="graph-group-header">{GROUP_LABELS[i]}</div>
                {group.map(renderRow)}
              </div>
            ),
          )
        )}
      </div>

      {confirmTarget && (
        <div className="confirm-overlay" onClick={() => setConfirmDeleteId(null)}>
          <div className="confirm-card" onClick={(e) => e.stopPropagation()}>
            <div className="confirm-title">Delete this conversation?</div>
            <div className="confirm-sub">
              “{displayTitle(confirmTarget)}” — this can’t be undone.
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
