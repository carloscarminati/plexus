import { useState } from "react";
import { ReasoningView } from "./ReasoningView";
import type { ReasoningSession } from "./reasoning-view";
import type { AdjudicationDecision } from "./contract";

// DEV-ONLY panel (ADR-0002 Rx): paste a case → run the reasoning recipe → render the
// structured-argument view of the persisted graph. Not a product flow (raw case text,
// no conversation-node linkage) — a minimal way to reach + see the engine.
export function ReasoningDevPanel({
  session,
  onRun,
  onAdjudicate,
  onClose,
}: {
  session: ReasoningSession;
  onRun: (caseText: string) => void;
  onAdjudicate: (graphId: string, decision: AdjudicationDecision, note?: string) => void;
  onClose: () => void;
}) {
  const [caseText, setCaseText] = useState("");
  const busy = session.status === "running" || session.status === "loading";

  return (
    <div className="settings-overlay" onClick={onClose}>
      <div className="settings-modal reasoning-modal" onClick={(e) => e.stopPropagation()}>
        <div className="settings-head">
          <span className="settings-title">Reasoning <span className="reasoning-dev-tag">dev</span></span>
          <button className="settings-close" onClick={onClose} aria-label="Close">✕</button>
        </div>

        <div className="settings-body">
          <textarea
            className="settings-input reasoning-case"
            rows={4}
            placeholder="Paste a case to investigate…"
            value={caseText}
            onChange={(e) => setCaseText(e.currentTarget.value)}
          />
          <div className="settings-row">
            <button className="btn-primary" disabled={!caseText.trim() || busy} onClick={() => onRun(caseText.trim())}>
              {busy ? "Running…" : "Run recipe"}
            </button>
          </div>

          {session.status === "error" && <div className="error-bar">{session.error}</div>}
          {session.status === "ready" && session.graph && (
            <ReasoningView
              graph={session.graph}
              diagnostics={session.diagnostics}
              openUncertainties={session.openUncertainties}
              adjudication={session.adjudication}
              onAdjudicate={(decision, note) => onAdjudicate(session.graph!.id, decision, note)}
            />
          )}
        </div>
      </div>
    </div>
  );
}
