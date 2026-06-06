import type { ModelInfo, RoutingPolicy, RoutingObjective } from "./contract";
import { shortModel } from "./format";

// The single, unified routing-policy control (R1 §6). Used both for the session
// default (topbar) and per-node override (detail pane). Manual mode reveals a
// model picker over the curated candidate set; auto modes pick cost/quality/
// balanced. `allowInherit` adds an "Inherit" option (null) for the per-node use.
type Mode = "inherit" | "manual" | "auto-cost" | "auto-quality" | "auto-balanced";

function modeOf(value: RoutingPolicy | null): Mode {
  if (!value) return "inherit";
  if (value.kind === "manual") return "manual";
  return `auto-${value.objective}` as Mode;
}

export function PolicyPicker({
  value,
  onChange,
  models,
  allowInherit = false,
  label,
}: {
  value: RoutingPolicy | null;
  onChange: (policy: RoutingPolicy | null) => void;
  models: ModelInfo[];
  allowInherit?: boolean;
  label?: string;
}) {
  const mode = modeOf(value);
  const manualModel = value?.kind === "manual" ? value.modelId : (models[0]?.id ?? "claude-opus-4-8");

  const onMode = (m: Mode) => {
    switch (m) {
      case "inherit":
        return onChange(null);
      case "manual":
        return onChange({ kind: "manual", modelId: manualModel });
      default:
        return onChange({ kind: "auto", objective: m.replace("auto-", "") as RoutingObjective });
    }
  };

  return (
    <div className="policy-picker">
      {label && <span className="policy-label">{label}</span>}
      <select value={mode} onChange={(e) => onMode(e.currentTarget.value as Mode)}>
        {allowInherit && <option value="inherit">Inherit</option>}
        <option value="manual">Manual</option>
        <option value="auto-cost">Auto · cost</option>
        <option value="auto-quality">Auto · quality</option>
        <option value="auto-balanced">Auto · balanced</option>
      </select>
      {mode === "manual" && (
        <select value={manualModel} onChange={(e) => onChange({ kind: "manual", modelId: e.currentTarget.value })}>
          {models.length === 0 && <option value={manualModel}>{shortModel(manualModel)}</option>}
          {models.map((m) => (
            <option key={m.id} value={m.id}>
              {shortModel(m.id)} · {m.tier} · ${m.costInPerMTok}/{m.costOutPerMTok}
            </option>
          ))}
        </select>
      )}
    </div>
  );
}
