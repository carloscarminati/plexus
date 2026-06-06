import { useEffect, useRef, useState, type FormEvent } from "react";
import { BlockView } from "./blocks/BlockView";
import { CanvasView } from "./CanvasView";
import { PolicyPicker } from "./PolicyPicker";
import { useSidecar } from "./useSidecar";
import { formatCost } from "./format";
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
