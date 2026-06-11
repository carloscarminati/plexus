import { useState, type ReactNode } from "react";
import type { Adjudication, AdjudicationDecision, Graph, ReasoningDiagnostic } from "./contract";
import { buildArgumentView, deriveReviewState } from "./reasoning-view";

// Renders the structured-argument view from the pure view-model. Surfaces the
// server-computed R1 diagnostics inline — a flagged conclusion is shown flagged. When an
// onAdjudicate handler is given, the human decision seam (Rx.2.0) renders below the
// argument — additive, beside the unchanged reasoning, never folded into it.
export function ReasoningView({
  graph,
  diagnostics,
  openUncertainties,
  adjudication,
  onAdjudicate,
}: {
  graph: Graph;
  diagnostics: ReasoningDiagnostic[];
  openUncertainties: string[];
  adjudication?: Adjudication | null;
  onAdjudicate?: (decision: AdjudicationDecision, note?: string) => void;
}) {
  const v = buildArgumentView(graph, diagnostics, openUncertainties);
  const reviewState = deriveReviewState(v.diagnostics, adjudication);
  const warnCount = v.diagnostics.filter((d) => d.severity === "warn").length;

  return (
    <div className="reasoning-view">
      {reviewState === "requires_review" && (
        <div className="review-banner requires-review">
          <strong>⚠ Human review required</strong>
          <span>The machine flagged this reasoning and could not auto-resolve it — a human must review.</span>
        </div>
      )}
      {reviewState === "reviewed" && (
        <div className="review-banner reviewed">
          <strong>✓ Reviewed</strong>
          <span>Adjudicated by a human — the decision is recorded below; the flag remains for audit.</span>
        </div>
      )}
      {reviewState === "clean" && warnCount > 0 && (
        <div className="review-banner advisory">
          {warnCount} advisory warning{warnCount > 1 ? "s" : ""} surfaced — review not required.
        </div>
      )}

      {v.frame && (
        <Section title="Frame">
          <p className="reasoning-frame">{v.frame.text}</p>
        </Section>
      )}

      <Section title="Facts">
        {v.facts.map((f) => (
          <Item key={f.id} label={f.label} diags={f.diagnostics}>
            {f.text}{" "}
            <span className="reasoning-rel">
              — grounded in {f.sourceText ? `[${f.sourceRef}] ${f.sourceText}` : f.sourceKind ?? "?"}
            </span>
          </Item>
        ))}
      </Section>

      <Section title="Uncertainties">
        {v.uncertainties.map((u) => (
          <Item key={u.id} label={u.label} diags={u.diagnostics}>
            {u.text}{" "}
            {u.open ? (
              <span className="reasoning-open">OPEN</span>
            ) : (
              <span className="reasoning-rel">— addressed by {u.addressedBy.join(", ") || "—"}</span>
            )}
          </Item>
        ))}
      </Section>

      <Section title="Hypotheses">
        {v.hypotheses.map((h) => (
          <Item key={h.id} label={h.label} diags={h.diagnostics}>
            {h.text}
            {h.addresses.length > 0 && <span className="reasoning-rel"> — addresses {h.addresses.join(", ")}</span>}
          </Item>
        ))}
      </Section>

      <Section title="Evaluation">
        {v.evaluation.map((r) => (
          <Item key={r.hypothesisLabel} label={r.hypothesisLabel}>
            {r.weighings
              .map((w) => `${w.stance === "supports" ? "supported" : "refuted"} by ${w.factLabel}${w.weight != null ? ` (${w.weight})` : ""}`)
              .join(", ")}
          </Item>
        ))}
      </Section>

      {v.conclusion && (
        <Section title="Conclusion">
          <Item label="" diags={v.conclusion.diagnostics}>
            {v.conclusion.text}{" "}
            <span className="reasoning-rel">
              — selects {v.conclusion.selects ?? "—"}; cites {v.conclusion.cites.join(", ") || "—"}
            </span>
          </Item>
        </Section>
      )}

      {onAdjudicate && <AdjudicationPanel adjudication={adjudication} onAdjudicate={onAdjudicate} />}
    </div>
  );
}

// The human decision seam. Shows the current adjudication if one exists (decision + note
// + reviewer + when), and lets the reviewer record/update one. The flags above inform the
// reviewer but never gate this — accepting a clean argument is a valid audit act too.
function AdjudicationPanel({
  adjudication,
  onAdjudicate,
}: {
  adjudication?: Adjudication | null;
  onAdjudicate: (decision: AdjudicationDecision, note?: string) => void;
}) {
  const [note, setNote] = useState(adjudication?.note ?? "");

  return (
    <section className="reasoning-section reasoning-adjudication">
      <h4 className="reasoning-section-title">Adjudication</h4>
      {adjudication ? (
        <div className={`adjudication-current decision-${adjudication.decision}`}>
          <span className="adjudication-decision">
            {adjudication.decision === "accept" ? "✓ Accepted" : "✗ Rejected"}
          </span>
          {adjudication.note && <span className="adjudication-note">“{adjudication.note}”</span>}
          <span className="reasoning-rel">
            — {adjudication.reviewer}, {formatWhen(adjudication.timestamp)}
          </span>
        </div>
      ) : (
        <p className="reasoning-rel">No adjudication yet.</p>
      )}
      <textarea
        className="settings-input adjudication-input"
        rows={2}
        placeholder="Note (optional) — e.g. why you accept despite a flag"
        value={note}
        onChange={(e) => setNote(e.currentTarget.value)}
      />
      <div className="settings-row adjudication-actions">
        <button className="btn-accept" onClick={() => onAdjudicate("accept", note)}>
          {adjudication ? "Update → Accept" : "Accept"}
        </button>
        <button className="btn-reject" onClick={() => onAdjudicate("reject", note)}>
          {adjudication ? "Update → Reject" : "Reject"}
        </button>
      </div>
    </section>
  );
}

function formatWhen(iso: string): string {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString();
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="reasoning-section">
      <h4 className="reasoning-section-title">{title}</h4>
      {children}
    </section>
  );
}

function Item({ label, diags, children }: { label: string; diags?: ReasoningDiagnostic[]; children: ReactNode }) {
  return (
    <div className="reasoning-item">
      {label && <span className="reasoning-label">{label}</span>}
      <span className="reasoning-text">{children}</span>
      {diags?.map((d, i) => (
        <span key={i} className={`reasoning-diag sev-${d.severity}`} title={d.message}>
          {d.severity}: {d.code}
        </span>
      ))}
    </div>
  );
}
