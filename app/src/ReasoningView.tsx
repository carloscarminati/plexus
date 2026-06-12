import { useState, type ReactNode } from "react";
import type { Adjudication, AdjudicationDecision, Graph, ReasoningDiagnostic } from "./contract";
import { buildArgumentView, buildEvaluationMatrix, deriveReviewState, type EvaluationMatrix } from "./reasoning-view";

// Renders the structured-argument view from the pure view-model. Surfaces the
// server-computed R1 diagnostics inline — a flagged conclusion is shown flagged. When an
// onAdjudicate handler is given, the human decision seam (Rx.2.0) renders below the
// argument — additive, beside the unchanged reasoning, never folded into it.
export function ReasoningView({
  graph,
  diagnostics,
  openUncertainties,
  adjudication,
  hypothesisNets,
  onAdjudicate,
}: {
  graph: Graph;
  diagnostics: ReasoningDiagnostic[];
  openUncertainties: string[];
  adjudication?: Adjudication | null;
  hypothesisNets: Record<string, number>;
  onAdjudicate?: (decision: AdjudicationDecision, note?: string) => void;
}) {
  const v = buildArgumentView(graph, diagnostics, openUncertainties);
  const matrix = buildEvaluationMatrix(graph, hypothesisNets, v.diagnostics);
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
      {reviewState === "accepted" && (
        <div className="review-banner accepted">
          <strong>✓ Accepted</strong>
          <span>A reviewer accepted this reasoning — recorded below; the flag remains for audit.</span>
        </div>
      )}
      {reviewState === "rejected" && (
        <div className="review-banner rejected">
          <strong>✗ Rejected</strong>
          <span>A reviewer rejected this reasoning — recorded below; the flag remains for audit.</span>
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
        {/* R3: the facts × hypotheses matrix is the AUTHORITATIVE "why" — facts weigh on
            hypotheses, column nets (server-computed) total the verdict; rendered prominent. */}
        {matrix.factRows.length > 0 && <EvaluationMatrixTable matrix={matrix} />}
        {/* B: the model's rationale, SUBORDINATE to the matrix — an unverified note to
            cross-check against it, not the verdict's reasoning (it can contradict the selection). */}
        {v.evaluationNote && (
          <div className="reasoning-rationale-note">
            <span className="reasoning-rationale-label">{v.evaluationNote.label}</span>
            <p className="reasoning-rationale">{v.evaluationNote.text}</p>
          </div>
        )}
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

// R3 — the ACH matrix: facts (rows) × hypotheses (columns), cells = signed weights, column
// footer = the server net. The selected + best-weighted columns are marked, so a selected ≠
// best divergence (the same fact C's warn reports) is visible at a glance in the totals.
function EvaluationMatrixTable({ matrix }: { matrix: EvaluationMatrix }) {
  const fmt = (n: number) => (Math.round(n * 100) / 100).toString();
  return (
    <div className="reasoning-matrix-wrap">
      <table className="reasoning-matrix">
        <thead>
          <tr>
            <th className="reasoning-matrix-corner" />
            {matrix.hypCols.map((h) => (
              <th key={h.id} className={`${h.selected ? "selected" : ""} ${h.bestWeighted ? "best" : ""}`}>
                {h.label}
                {h.selected && <span className="reasoning-matrix-tag">selected</span>}
                {h.bestWeighted && !h.selected && <span className="reasoning-matrix-tag best">best</span>}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {matrix.factRows.map((f) => (
            <tr key={f.id}>
              <th>{f.label}</th>
              {f.cells.map((c, j) => (
                <td key={j} className={c == null ? "empty" : c > 0 ? "pos" : "neg"}>
                  {c == null ? "·" : `${c > 0 ? "+" : ""}${fmt(c)}`}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
        <tfoot>
          <tr>
            <th>net</th>
            {matrix.hypCols.map((h) => (
              <td key={h.id} className={`reasoning-matrix-net ${h.selected ? "selected" : ""} ${h.net < 0 ? "neg" : ""}`}>{fmt(h.net)}</td>
            ))}
          </tr>
        </tfoot>
      </table>
      {matrix.divergent && (
        <p className="reasoning-matrix-divergence">
          The selected hypothesis is not the best-weighted column — see the net totals.
        </p>
      )}
    </div>
  );
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
