import type { ModelInfo, ProviderView, RoutingPolicy, RoutingObjective } from "./contract";
import { shortModel } from "./format";

// The single, unified routing-policy control (R1 §6). Used both for the session
// default (topbar) and per-node override (detail pane). Manual mode reveals a
// model picker; auto modes pick cost/quality/balanced. `allowInherit` adds an
// "Inherit" option (null) for the per-node use.
//
// Provider-aware (#1): when openai-compatible providers are configured, Manual
// mode also shows a provider selector. Anthropic uses the curated model dropdown;
// an openai-compatible provider takes a free-text model id (no curated catalog),
// and the chosen providerId rides along on the policy so the turn routes there.
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
  providers = [],
  allowInherit = false,
  label,
}: {
  value: RoutingPolicy | null;
  onChange: (policy: RoutingPolicy | null) => void;
  models: ModelInfo[];
  providers?: ProviderView[];
  allowInherit?: boolean;
  label?: string;
}) {
  const mode = modeOf(value);
  const manualModel = value?.kind === "manual" ? value.modelId : (models[0]?.id ?? "claude-opus-4-8");
  const manualProviderId = value?.kind === "manual" ? (value.providerId ?? "") : "";

  // Anthropic is the implicit default (empty providerId → registry default). Only
  // surface the provider selector once there's somewhere else to route.
  const openaiProviders = providers.filter((p) => p.type === "openai-compatible" && p.enabled);
  const showProviderSelect = openaiProviders.length > 0;
  const selectedOpenai = openaiProviders.find((p) => p.id === manualProviderId);
  const isOpenai = !!selectedOpenai;

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

  const onProvider = (id: string) => {
    if (!id) return onChange({ kind: "manual", modelId: models[0]?.id ?? "claude-opus-4-8" });
    const p = openaiProviders.find((x) => x.id === id);
    onChange({ kind: "manual", modelId: p?.modelId ?? "", providerId: id });
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

      {mode === "manual" && showProviderSelect && (
        <select value={manualProviderId} onChange={(e) => onProvider(e.currentTarget.value)} title="Provider">
          <option value="">Anthropic</option>
          {openaiProviders.map((p) => (
            <option key={p.id} value={p.id}>
              {p.label || p.id}
            </option>
          ))}
        </select>
      )}

      {mode === "manual" &&
        (isOpenai ? (
          <input
            className="policy-model-input"
            value={manualModel}
            placeholder="model id"
            spellCheck={false}
            onChange={(e) => onChange({ kind: "manual", modelId: e.currentTarget.value, providerId: manualProviderId })}
          />
        ) : (
          <select value={manualModel} onChange={(e) => onChange({ kind: "manual", modelId: e.currentTarget.value })}>
            {models.length === 0 && <option value={manualModel}>{shortModel(manualModel)}</option>}
            {models.map((m) => (
              <option key={m.id} value={m.id}>
                {shortModel(m.id)} · {m.tier} · ${m.costInPerMTok}/{m.costOutPerMTok}
              </option>
            ))}
          </select>
        ))}
    </div>
  );
}
