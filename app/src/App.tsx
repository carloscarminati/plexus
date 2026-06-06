import { useEffect, useRef, useState, type FormEvent } from "react";
import { BlockView } from "./blocks/BlockView";
import { CanvasView } from "./CanvasView";
import { openUrl } from "@tauri-apps/plugin-opener";
import { PolicyPicker } from "./PolicyPicker";
import { useSidecar } from "./useSidecar";
import { formatCost } from "./format";
import { REPO_URL, APP_VERSION } from "./meta";
import "./App.css";

function App() {
  const {
    status,
    graph,
    pending,
    error,
    selectedId,
    select,
    models,
    sessionPolicy,
    setSessionPolicy,
    nodeOverrides,
    setNodeOverride,
    sendMessage,
    sendChoice,
  } = useSidecar();
  const [draft, setDraft] = useState("");
  const detailRef = useRef<HTMLDivElement>(null);

  const nodes = graph?.nodes ?? [];
  const selected = nodes.find((n) => n.id === selectedId) ?? null;
  const branching = selectedId != null && nodes.some((n) => n.parentId === selectedId);

  useEffect(() => {
    detailRef.current?.scrollTo({ top: detailRef.current.scrollHeight });
  }, [selectedId, selected?.blocks.length]);

  const doSend = () => {
    if (!draft.trim() || pending) return;
    sendMessage(draft);
    setDraft("");
  };
  const submit = (e: FormEvent) => {
    e.preventDefault();
    doSend();
  };

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">Plexus</div>
        <div className="topbar-right">
          <PolicyPicker
            label="Routing"
            value={sessionPolicy}
            onChange={(p) => p && setSessionPolicy(p)}
            models={models}
          />
          <div className={`status status-${status}`}>{status}</div>
          <button
            className="repo-link"
            title={`Plexus v${APP_VERSION} — view on GitHub`}
            aria-label="View on GitHub"
            onClick={() => openUrl(REPO_URL)}
          >
            <svg viewBox="0 0 16 16" width="18" height="18" fill="currentColor" aria-hidden="true">
              <path d="M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.01 8.01 0 0 0 16 8c0-4.42-3.58-8-8-8Z" />
            </svg>
          </button>
        </div>
      </header>

      <div className="workspace">
        <div className="canvas-pane">
          {graph && nodes.length > 0 ? (
            <CanvasView graph={graph} selectedId={selectedId} pending={pending} onSelect={select} />
          ) : (
            <div className="empty">Send a message to start the graph.</div>
          )}
        </div>

        <aside className="detail-pane">
          <div className="detail-scroll" ref={detailRef}>
            {selected ? (
              <>
                <div className="detail-head">
                  <span className="detail-role">{selected.role}</span>
                  {selected.meta?.model && (
                    <span className="detail-meta" title={selected.meta.reason ?? undefined}>
                      {selected.meta.model}
                      {selected.meta.costUsd != null && ` · ${formatCost(selected.meta.costUsd)}`}
                      {selected.meta.tokensOut != null &&
                        ` · ${selected.meta.tokensIn ?? "?"}→${selected.meta.tokensOut} tok`}
                      {selected.meta.latencyMs != null && ` · ${(selected.meta.latencyMs / 1000).toFixed(1)}s`}
                    </span>
                  )}
                </div>
                <div className="branch-policy">
                  <PolicyPicker
                    label="Branch from here"
                    value={nodeOverrides[selected.id] ?? null}
                    onChange={(p) => setNodeOverride(selected.id, p)}
                    models={models}
                    allowInherit
                  />
                  {selected.meta?.reason && <span className="branch-reason">{selected.meta.reason}</span>}
                </div>
                <div className="detail-blocks">
                  {selected.blocks.map((block, i) => (
                    <BlockView
                      key={i}
                      block={block}
                      onChoice={
                        selected.role === "assistant"
                          ? (opt) => sendChoice(selected.id, opt)
                          : undefined
                      }
                    />
                  ))}
                </div>
              </>
            ) : (
              <div className="empty small">Select a node, or send a message to begin.</div>
            )}
          </div>

          {error && <div className="error-bar">{error}</div>}

          <form className="composer" onSubmit={submit}>
            {branching && <div className="branch-hint">↳ branching from the selected {selected?.role} node</div>}
            <div className="composer-row">
              <textarea
                autoFocus
                value={draft}
                onChange={(e) => setDraft(e.currentTarget.value)}
                onKeyDown={(e) => {
                  if (e.key === "Enter" && !e.shiftKey) {
                    e.preventDefault();
                    doSend();
                  }
                }}
                placeholder={
                  status !== "online"
                    ? "Connecting to sidecar…"
                    : selected
                      ? `Reply from this ${selected.role} node…`
                      : "Send a message…"
                }
                disabled={status !== "online"}
                rows={3}
              />
              <button type="submit" disabled={status !== "online" || !!pending || !draft.trim()}>
                {pending ? "…" : "Send"}
              </button>
            </div>
          </form>
        </aside>
      </div>
    </div>
  );
}

export default App;
