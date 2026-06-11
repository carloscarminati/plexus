import { useEffect, useRef, useState, type FormEvent } from "react";
import { BlockView } from "./blocks/BlockView";
import { CanvasView } from "./CanvasView";
import { openUrl } from "@tauri-apps/plugin-opener";
import { PolicyPicker } from "./PolicyPicker";
import { GraphSidebar } from "./GraphSidebar";
import { SettingsModal } from "./SettingsModal";
import { ReasoningDevPanel } from "./ReasoningDevPanel";
import { ComposeDrawer } from "./ComposeDrawer";
import { PlexusLogo } from "./components/PlexusLogo";
import { blocksToMarkdown } from "./compose/markdown";
import { useSidecar } from "./useSidecar";
import { formatCost, shortModel } from "./format";
import type { RoutingPolicy } from "./contract";
import { REPO_URL, APP_VERSION } from "./meta";
import "./App.css";

// Canvas/detail splitter bounds (px). Detail width is user-resizable; neither pane
// is allowed to collapse below its minimum.
const DETAIL_MIN = 360;
const DETAIL_DEFAULT = 440;
const CANVAS_MIN = 320;
const DIVIDER_W = 6;

// Clamp a detail width to the viewport so a small window can't wedge the layout.
const clampDetailToViewport = (w: number) =>
  Math.max(DETAIL_MIN, Math.min(w, Math.max(DETAIL_MIN, window.innerWidth - CANVAS_MIN - DIVIDER_W)));

function App() {
  const {
    status,
    graph,
    graphs,
    newGraph,
    openGraph,
    renameGraph,
    deleteGraph,
    pinGraph,
    pending,
    error,
    selectedIds,
    selectedId,
    clickNode,
    models,
    sessionPolicy,
    setSessionPolicy,
    nodeOverrides,
    setNodeOverride,
    confirm,
    respondConfirm,
    sendMessage,
    sendChoice,
    escalate,
    synthesize,
    settings,
    setGeneralSettings,
    setDefaultPolicy,
    deleteAnthropicKey,
    setMcpServer,
    deleteMcpServer,
    setProvider,
    deleteProvider,
    reasoning,
    runReasoning,
  } = useSidecar();
  const [draft, setDraft] = useState("");
  const [showSettings, setShowSettings] = useState(false);
  const [showReasoning, setShowReasoning] = useState(false);
  const [showCompose, setShowCompose] = useState(false);
  // Conversation sidebar collapse — persisted across reloads (localStorage works in
  // the Tauri webview).
  const [sidebarCollapsed, setSidebarCollapsed] = useState<boolean>(() => {
    try {
      return localStorage.getItem("plexus.sidebar.collapsed") === "true";
    } catch {
      return false;
    }
  });
  // Resizable detail pane width — persisted, clamped to the viewport on load.
  const [detailWidth, setDetailWidth] = useState<number>(() => {
    try {
      const v = parseInt(localStorage.getItem("plexus.detailPane.width") ?? "", 10);
      return Number.isFinite(v) ? clampDetailToViewport(v) : DETAIL_DEFAULT;
    } catch {
      return DETAIL_DEFAULT;
    }
  });
  const workspaceRef = useRef<HTMLDivElement>(null);
  const canvasRef = useRef<HTMLDivElement>(null);
  const dragBounds = useRef<{ right: number; canvasLeft: number } | null>(null);
  const [copiedNode, setCopiedNode] = useState(false);
  // Escalate target: defaults to Auto-quality (top tier); the picker can override.
  const [escalatePolicy, setEscalatePolicy] = useState<RoutingPolicy>({ kind: "auto", objective: "quality" });
  const detailRef = useRef<HTMLDivElement>(null);

  const nodes = graph?.nodes ?? [];
  const selected = nodes.find((n) => n.id === selectedId) ?? null;
  const merging = selectedIds.length >= 2; // P2 DAG merge
  const branching = !merging && selectedId != null && nodes.some((n) => n.parentId === selectedId);

  // X0 COMPOSE: harvest the selected nodes' blocks, ordered by createdAt (deterministic).
  const composeNodes = selectedIds
    .map((id) => nodes.find((n) => n.id === id))
    .filter((n): n is NonNullable<typeof n> => n != null)
    .sort((a, b) => a.createdAt.localeCompare(b.createdAt));
  const composeBlocks = composeNodes.flatMap((n) => n.blocks);

  // Soft suggestion (§4.2): a node auto-routed to a non-top tier can be escalated
  // to a stronger model in one click.
  const selTier = models.find((m) => m.id === selected?.meta?.model)?.tier;
  const suggestEscalate =
    selected?.role === "assistant" &&
    (selected.meta?.policy ?? "").startsWith("auto:") &&
    (selTier === "small" || selTier === "mid");

  useEffect(() => {
    detailRef.current?.scrollTo({ top: detailRef.current.scrollHeight });
    setCopiedNode(false);
  }, [selectedId, selected?.blocks.length]);

  useEffect(() => {
    try {
      localStorage.setItem("plexus.sidebar.collapsed", String(sidebarCollapsed));
    } catch {
      /* ignore (private mode / disabled storage) */
    }
  }, [sidebarCollapsed]);

  useEffect(() => {
    try {
      localStorage.setItem("plexus.detailPane.width", String(Math.round(detailWidth)));
    } catch {
      /* ignore */
    }
  }, [detailWidth]);

  // Keep the detail width sane against the real geometry: on mount, on window
  // resize, and when the sidebar toggles (which changes the canvas region). Idempotent.
  useEffect(() => {
    const clamp = () => {
      const ws = workspaceRef.current?.getBoundingClientRect();
      const cv = canvasRef.current?.getBoundingClientRect();
      setDetailWidth((w) => {
        if (ws && cv) {
          const maxDetail = Math.max(DETAIL_MIN, ws.right - cv.left - DIVIDER_W - CANVAS_MIN);
          return Math.min(w, maxDetail);
        }
        return clampDetailToViewport(w);
      });
    };
    clamp();
    window.addEventListener("resize", clamp);
    return () => window.removeEventListener("resize", clamp);
  }, [sidebarCollapsed]);

  // ── Canvas/detail splitter (hand-rolled, pointer-capture drag) ──────────────
  const onDividerDown = (e: React.PointerEvent<HTMLDivElement>) => {
    const ws = workspaceRef.current?.getBoundingClientRect();
    const cv = canvasRef.current?.getBoundingClientRect();
    if (!ws || !cv) return;
    // Bounds are fixed for the drag: detail grows leftward from the workspace's
    // right edge; the canvas keeps at least CANVAS_MIN starting at its current left.
    dragBounds.current = { right: ws.right, canvasLeft: cv.left };
    e.currentTarget.setPointerCapture(e.pointerId);
    document.body.classList.add("col-resizing");
  };
  const onDividerMove = (e: React.PointerEvent<HTMLDivElement>) => {
    const b = dragBounds.current;
    if (!b) return;
    const maxDetail = Math.max(DETAIL_MIN, b.right - b.canvasLeft - DIVIDER_W - CANVAS_MIN);
    setDetailWidth(Math.max(DETAIL_MIN, Math.min(b.right - e.clientX, maxDetail)));
  };
  const onDividerUp = (e: React.PointerEvent<HTMLDivElement>) => {
    dragBounds.current = null;
    try {
      e.currentTarget.releasePointerCapture(e.pointerId);
    } catch {
      /* already released */
    }
    document.body.classList.remove("col-resizing");
  };

  // Cmd/Ctrl+\ toggles the sidebar — but never while typing in a field.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (!(e.metaKey || e.ctrlKey) || e.key !== "\\") return;
      const el = document.activeElement as HTMLElement | null;
      const tag = el?.tagName;
      if (tag === "INPUT" || tag === "TEXTAREA" || el?.isContentEditable) return;
      e.preventDefault();
      setSidebarCollapsed((v) => !v);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  const doSend = () => {
    if (!draft.trim() || pending) return;
    sendMessage(draft);
    setDraft("");
  };
  const submit = (e: FormEvent) => {
    e.preventDefault();
    doSend();
  };

  // Copy the selected node's blocks as Markdown (reuses the X0 serializer).
  const copyNodeMarkdown = async () => {
    if (!selected) return;
    try {
      await navigator.clipboard.writeText(blocksToMarkdown(selected.blocks));
      setCopiedNode(true);
      setTimeout(() => setCopiedNode(false), 1500);
    } catch {
      /* clipboard unavailable / rejected — no-op */
    }
  };

  return (
    <div className="app">
      <header className="topbar">
        <div className="topbar-left">
          <button
            className="sidebar-toggle"
            title={`${sidebarCollapsed ? "Show" : "Hide"} conversations (⌘\\)`}
            aria-label="Toggle conversation sidebar"
            aria-pressed={!sidebarCollapsed}
            onClick={() => setSidebarCollapsed((v) => !v)}
          >
            <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
              <rect x="3" y="4" width="18" height="16" rx="2" />
              <line x1="9" y1="4" x2="9" y2="20" />
            </svg>
          </button>
          <div className="brand">Plexus</div>
        </div>
        <div className="topbar-right">
          <PolicyPicker
            label="Routing"
            value={sessionPolicy}
            onChange={(p) => p && setSessionPolicy(p)}
            models={models}
            providers={settings?.providers ?? []}
          />
          <button
            className="compose-btn"
            disabled={selectedIds.length === 0}
            title={selectedIds.length === 0 ? "Select node(s) on the canvas to compose" : "Compose a deliverable from the selection"}
            onClick={() => setShowCompose(true)}
          >
            ⊞ Compose{selectedIds.length > 0 ? ` (${selectedIds.length})` : ""}
          </button>
          <div className={`status status-${status}`}>{status}</div>
          <button
            className="repo-link reasoning-dev-btn"
            title="Reasoning (dev) — run a recipe and inspect the argument graph"
            aria-label="Reasoning (dev)"
            onClick={() => setShowReasoning(true)}
          >
            ⚖ dev
          </button>
          <button
            className="repo-link"
            title="Settings"
            aria-label="Settings"
            onClick={() => setShowSettings(true)}
          >
            <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" strokeWidth="2" aria-hidden="true">
              <circle cx="12" cy="12" r="3" />
              <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1Z" />
            </svg>
          </button>
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

      <div className="workspace" ref={workspaceRef}>
        {!sidebarCollapsed && (
          <GraphSidebar
            graphs={graphs}
            activeId={graph?.id ?? null}
            onNew={newGraph}
            onOpen={openGraph}
            onRename={renameGraph}
            onDelete={deleteGraph}
            onPin={pinGraph}
          />
        )}

        <div className="canvas-pane" ref={canvasRef}>
          {graph && nodes.length > 0 ? (
            <CanvasView graph={graph} selectedIds={selectedIds} pending={pending} onClickNode={clickNode} />
          ) : (
            <div className="empty empty-brand">
              <PlexusLogo size={56} variant="mark" />
              <div>Send a message to start the graph.</div>
            </div>
          )}
        </div>

        <div
          className="pane-divider"
          role="separator"
          aria-orientation="vertical"
          aria-label="Resize detail pane"
          title="Drag to resize · double-click to reset"
          onPointerDown={onDividerDown}
          onPointerMove={onDividerMove}
          onPointerUp={onDividerUp}
          onDoubleClick={() => setDetailWidth(DETAIL_DEFAULT)}
        />

        <aside className="detail-pane" style={{ flex: `0 0 ${detailWidth}px`, width: detailWidth }}>
          <div className="detail-scroll" ref={detailRef}>
            {selected ? (
              <>
                <div className="detail-head">
                  <span className={`detail-role ${selected.kind === "deliverable" ? "detail-brief" : ""}`}>
                    {selected.kind === "deliverable" ? "◆ decision brief" : selected.role}
                  </span>
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
                    providers={settings?.providers ?? []}
                    allowInherit
                  />
                  {selected.meta?.reason && <span className="branch-reason">{selected.meta.reason}</span>}
                </div>
                <div className="node-actions">
                  <button className="node-copy" onClick={copyNodeMarkdown} title="Copy this node as Markdown">
                    {copiedNode ? "Copied ✓" : "⧉ Copy"}
                  </button>
                </div>
                {selected.role === "assistant" && (
                  <div className="escalate-box">
                    <div className="escalate-row">
                      <PolicyPicker
                        label="Escalate with"
                        value={escalatePolicy}
                        onChange={(p) => setEscalatePolicy(p ?? { kind: "auto", objective: "quality" })}
                        models={models}
                        providers={settings?.providers ?? []}
                      />
                      <button
                        className="escalate-btn"
                        disabled={!!pending}
                        title="Re-run this turn as a sibling branch with the chosen model — answers compared side by side"
                        onClick={() => escalate(selected.id, escalatePolicy)}
                      >
                        ⬆ Escalate
                      </button>
                    </div>
                    {suggestEscalate && (
                      <button
                        className="escalate-suggest"
                        disabled={!!pending}
                        onClick={() => escalate(selected.id, { kind: "auto", objective: "quality" })}
                      >
                        Auto picked {shortModel(selected.meta!.model!)} here — escalate to a stronger model?
                      </button>
                    )}
                  </div>
                )}
                {selected.meta?.toolCalls && selected.meta.toolCalls.length > 0 && (
                  <div className="tool-calls">
                    <div className="tool-calls-head">tool calls</div>
                    {selected.meta.toolCalls.map((tc, i) => (
                      <div key={i} className={`tool-call ${tc.approved ? "" : "denied"}`}>
                        <div className="tool-call-head">
                          <span className="tool-call-name">🔧 {tc.tool}</span>
                          <span className="tool-call-tags">
                            <span className="tool-chip">{tc.serverId}</span>
                            <span className="tool-chip">{tc.readOnly ? "read-only" : "side-effecting"}</span>
                            {!tc.approved && <span className="tool-chip denied">denied</span>}
                          </span>
                        </div>
                        <div className="tool-call-args">{JSON.stringify(tc.args)}</div>
                        <div className="tool-call-result">{tc.resultSummary}</div>
                      </div>
                    ))}
                  </div>
                )}
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
            {merging && (
              <div className="branch-hint">⤚ merging {selectedIds.length} nodes — the new turn gets the union of their context</div>
            )}
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
                    : merging
                      ? `Merge ${selectedIds.length} nodes into a new turn…`
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

      {showCompose && (
        <ComposeDrawer
          blocks={composeBlocks}
          nodeCount={composeNodes.length}
          title={graph?.title}
          onSynthesize={() => {
            synthesize(selectedIds, sessionPolicy);
            setShowCompose(false);
          }}
          onClose={() => setShowCompose(false)}
        />
      )}

      {showSettings && (
        <SettingsModal
          settings={settings}
          models={models}
          onClose={() => setShowSettings(false)}
          setGeneralSettings={setGeneralSettings}
          setDefaultPolicy={setDefaultPolicy}
          deleteAnthropicKey={deleteAnthropicKey}
          setMcpServer={setMcpServer}
          deleteMcpServer={deleteMcpServer}
          setProvider={setProvider}
          deleteProvider={deleteProvider}
        />
      )}

      {showReasoning && (
        <ReasoningDevPanel session={reasoning} onRun={runReasoning} onClose={() => setShowReasoning(false)} />
      )}

      {confirm && (
        <div className="confirm-overlay">
          <div className="confirm-card">
            <div className="confirm-title">Approve tool call?</div>
            <div className="confirm-sub">
              The model wants to run a {confirm.readOnly ? "read-only" : "side-effecting"} tool on{" "}
              <strong>{confirm.serverName || confirm.serverId}</strong>.
            </div>
            <div className="confirm-tool">🔧 {confirm.tool}</div>
            <pre className="confirm-args">{JSON.stringify(confirm.args, null, 2)}</pre>
            <div className="confirm-actions">
              <button className="confirm-deny" onClick={() => respondConfirm(false)}>
                Deny
              </button>
              <button className="confirm-approve" onClick={() => respondConfirm(true)}>
                Approve &amp; run
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

export default App;
