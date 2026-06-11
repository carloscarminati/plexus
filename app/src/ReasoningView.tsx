import type { ReactNode } from "react";
import type { Graph, ReasoningDiagnostic } from "./contract";
import { buildArgumentView } from "./reasoning-view";

// Renders the structured-argument view from the pure view-model. Surfaces the
// server-computed R1 diagnostics inline — a flagged conclusion is shown flagged.
export function ReasoningView({
  graph,
  diagnostics,
  openUncertainties,
}: {
  graph: Graph;
  diagnostics: ReasoningDiagnostic[];
  openUncertainties: string[];
}) {
  const v = buildArgumentView(graph, diagnostics, openUncertainties);

  return (
    <div className="reasoning-view">
      {v.diagnostics.length > 0 && (
        <div className="reasoning-banner">
          ⚠ {v.diagnostics.length} issue{v.diagnostics.length > 1 ? "s" : ""} caught by the reasoning invariants
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
