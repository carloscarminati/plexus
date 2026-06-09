import { useState } from "react";
import type { AppSettingsView, McpServerView, ModelInfo, ProviderView, RoutingPolicy } from "./contract";
import { PolicyPicker } from "./PolicyPicker";

// Consolidated Settings panel. It edits existing config (settings.json, the MCP
// registry) and writes secrets to the keychain via the sidecar — it never holds
// or displays secrets. The server re-emits a fresh snapshot after every change.
export function SettingsModal({
  settings,
  models,
  onClose,
  setGeneralSettings,
  setDefaultPolicy,
  deleteAnthropicKey,
  setMcpServer,
  deleteMcpServer,
  setProvider,
  deleteProvider,
}: {
  settings: AppSettingsView | null;
  models: ModelInfo[];
  onClose: () => void;
  setGeneralSettings: (confirmTimeoutSeconds: number) => void;
  setDefaultPolicy: (policy: RoutingPolicy) => void;
  deleteAnthropicKey: () => void;
  setMcpServer: (server: McpServerView, httpCredential?: string) => void;
  deleteMcpServer: (id: string) => void;
  setProvider: (provider: ProviderView, apiKey?: string) => void;
  deleteProvider: (id: string) => void;
}) {
  return (
    <div className="settings-overlay" onClick={onClose}>
      <div className="settings-modal" onClick={(e) => e.stopPropagation()}>
        <div className="settings-head">
          <span className="settings-title">Settings</span>
          <button className="settings-close" onClick={onClose} aria-label="Close">
            ✕
          </button>
        </div>

        {!settings ? (
          <div className="settings-body settings-loading">Loading…</div>
        ) : (
          <div className="settings-body">
            <ProvidersSection
              providers={settings.providers}
              onSave={setProvider}
              onDelete={deleteProvider}
              onRemoveAnthropicKey={deleteAnthropicKey}
            />
            <RoutingSection
              value={settings.defaultPolicy}
              models={models}
              providers={settings.providers}
              onChange={setDefaultPolicy}
            />
            <GeneralSection value={settings.confirmTimeoutSeconds} onSave={setGeneralSettings} />
            <McpSection servers={settings.mcpServers} onSave={setMcpServer} onDelete={deleteMcpServer} />
          </div>
        )}
      </div>
    </div>
  );
}

// ── Providers (multi-provider execution, #1) ────────────────────────────────
// Anthropic is the built-in default (key only). OpenAI-compatible providers add
// a base URL + default model id + key (OpenAI, gateways, Ollama, …). Keys go to
// the keychain by provider id; config (no secrets) to providers.json.
function ProvidersSection({
  providers,
  onSave,
  onDelete,
  onRemoveAnthropicKey,
}: {
  providers: ProviderView[];
  onSave: (provider: ProviderView, apiKey?: string) => void;
  onDelete: (id: string) => void;
  onRemoveAnthropicKey: () => void;
}) {
  const [adding, setAdding] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [confirmId, setConfirmId] = useState<string | null>(null);
  const [key, setKey] = useState<Record<string, string>>({});

  const setKeyFor = (id: string, v: string) => setKey((k) => ({ ...k, [id]: v }));

  return (
    <section className="settings-section">
      <h3>Providers</h3>

      {providers.map((p) =>
        editingId === p.id ? (
          <ProviderForm
            key={p.id}
            initial={p}
            onCancel={() => setEditingId(null)}
            onSubmit={(prov, apiKey) => {
              onSave(prov, apiKey);
              setEditingId(null);
            }}
          />
        ) : (
          <div key={p.id} className="mcp-item">
            <div className="mcp-main">
              <div className="mcp-name">
                {p.label || p.id}
                <span className={`key-status ${p.keySet ? "ok" : "missing"}`}>
                  {p.keySet ? "🔑 key set" : "no key"}
                </span>
              </div>
              <div className="mcp-sub">
                {p.type === "anthropic"
                  ? `anthropic${p.modelId ? ` · ${p.modelId}` : ""}`
                  : `openai-compatible · ${p.baseUrl ?? "—"}${p.modelId ? ` · ${p.modelId}` : ""}`}
              </div>
            </div>

            {/* Inline key field — the secret never round-trips back from the server. */}
            <input
              type="password"
              className="settings-input narrow"
              placeholder={p.keySet ? "replace key…" : p.type === "anthropic" ? "sk-ant-…" : "api key"}
              value={key[p.id] ?? ""}
              autoComplete="off"
              onChange={(e) => setKeyFor(p.id, e.currentTarget.value)}
            />
            <button
              className="btn-mini"
              disabled={!(key[p.id] ?? "").trim()}
              onClick={() => {
                onSave(p, (key[p.id] ?? "").trim());
                setKeyFor(p.id, "");
              }}
            >
              save key
            </button>

            {p.type === "openai-compatible" && (
              <button className="btn-mini" onClick={() => setEditingId(p.id)}>
                edit
              </button>
            )}

            {/* Anthropic is the built-in default: it can drop its key but isn't deleted. */}
            {p.type === "anthropic"
              ? p.keySet && (
                  <button className="btn-mini danger" title="Remove key" onClick={onRemoveAnthropicKey}>
                    ✕
                  </button>
                )
              : confirmId === p.id
                ? (
                    <span className="mcp-confirm">
                      <button
                        className="btn-danger sm"
                        onClick={() => {
                          onDelete(p.id);
                          setConfirmId(null);
                        }}
                      >
                        delete
                      </button>
                      <button className="btn-mini" onClick={() => setConfirmId(null)}>
                        ✕
                      </button>
                    </span>
                  )
                : (
                    <button className="btn-mini danger" title="Delete provider" onClick={() => setConfirmId(p.id)}>
                      ✕
                    </button>
                  )}
          </div>
        ),
      )}

      {adding ? (
        <ProviderForm
          onCancel={() => setAdding(false)}
          onSubmit={(prov, apiKey) => {
            onSave(prov, apiKey);
            setAdding(false);
          }}
        />
      ) : (
        <button className="btn-secondary" onClick={() => setAdding(true)}>
          + Add provider
        </button>
      )}
      <p className="settings-hint">
        Keys are stored in the OS keychain by provider id — never in a file or shown back here.
        OpenAI-compatible providers run on Manual routing; pick them in the model picker.
      </p>
    </section>
  );
}

// Add/edit form for an OpenAI-compatible provider (Anthropic is the built-in
// default and isn't created here). Editing an existing one keeps its key unless a
// new one is entered.
function ProviderForm({
  initial,
  onSubmit,
  onCancel,
}: {
  initial?: ProviderView;
  onSubmit: (provider: ProviderView, apiKey?: string) => void;
  onCancel: () => void;
}) {
  const [id, setId] = useState(initial?.id ?? "");
  const [label, setLabel] = useState(initial?.label ?? "");
  const [baseUrl, setBaseUrl] = useState(initial?.baseUrl ?? "");
  const [modelId, setModelId] = useState(initial?.modelId ?? "");
  const [apiKey, setApiKey] = useState("");
  const editing = !!initial;

  const valid = id.trim() && baseUrl.trim();

  const submit = () => {
    const provider: ProviderView = {
      id: id.trim(),
      type: "openai-compatible",
      label: label.trim() || id.trim(),
      baseUrl: baseUrl.trim(),
      modelId: modelId.trim() || undefined,
      enabled: initial?.enabled ?? true,
      keySet: initial?.keySet,
    };
    onSubmit(provider, apiKey.trim() || undefined);
  };

  return (
    <div className="mcp-form">
      <div className="mcp-form-row">
        <input
          className="settings-input"
          placeholder="id (e.g. openai)"
          value={id}
          disabled={editing}
          onChange={(e) => setId(e.currentTarget.value)}
        />
        <input
          className="settings-input"
          placeholder="label"
          value={label}
          onChange={(e) => setLabel(e.currentTarget.value)}
        />
      </div>
      <div className="mcp-form-row">
        <input
          className="settings-input grow"
          placeholder="base URL (e.g. https://api.openai.com/v1)"
          value={baseUrl}
          onChange={(e) => setBaseUrl(e.currentTarget.value)}
        />
        <input
          className="settings-input"
          placeholder="default model (e.g. gpt-4o-mini)"
          value={modelId}
          onChange={(e) => setModelId(e.currentTarget.value)}
        />
      </div>
      <div className="mcp-form-row">
        <input
          type="password"
          className="settings-input grow"
          placeholder={initial?.keySet ? "replace api key…" : "api key"}
          value={apiKey}
          autoComplete="off"
          onChange={(e) => setApiKey(e.currentTarget.value)}
        />
      </div>
      <div className="mcp-form-actions">
        <button className="btn-mini" onClick={onCancel}>
          cancel
        </button>
        <button className="btn-primary" disabled={!valid} onClick={submit}>
          {editing ? "Save" : "Add"}
        </button>
      </div>
    </div>
  );
}

// ── Routing default (topbar / per-node override this) ───────────────────────
function RoutingSection({
  value,
  models,
  providers,
  onChange,
}: {
  value: RoutingPolicy;
  models: ModelInfo[];
  providers: ProviderView[];
  onChange: (p: RoutingPolicy) => void;
}) {
  return (
    <section className="settings-section">
      <h3>Routing</h3>
      <div className="settings-row">
        <span className="settings-label">Default policy for new conversations</span>
        <PolicyPicker value={value} models={models} providers={providers} onChange={(p) => p && onChange(p)} />
      </div>
      <p className="settings-hint">The topbar and per-node pickers override this default per turn.</p>
    </section>
  );
}

// ── General ─────────────────────────────────────────────────────────────────
function GeneralSection({ value, onSave }: { value: number; onSave: (n: number) => void }) {
  const [secs, setSecs] = useState(String(value));
  const commit = () => {
    const n = parseInt(secs, 10);
    if (Number.isFinite(n) && n > 0 && n !== value) onSave(n);
    else setSecs(String(value));
  };
  return (
    <section className="settings-section">
      <h3>General</h3>
      <div className="settings-row">
        <span className="settings-label">Tool-confirmation timeout (seconds)</span>
        <input
          type="number"
          min={1}
          className="settings-input narrow"
          value={secs}
          onChange={(e) => setSecs(e.currentTarget.value)}
          onBlur={commit}
          onKeyDown={(e) => e.key === "Enter" && commit()}
        />
      </div>
      <p className="settings-hint">
        How long an unanswered MCP tool-confirmation waits before the turn cancels. Default 120.
      </p>
    </section>
  );
}

// ── MCP servers ─────────────────────────────────────────────────────────────
function McpSection({
  servers,
  onSave,
  onDelete,
}: {
  servers: McpServerView[];
  onSave: (s: McpServerView, cred?: string) => void;
  onDelete: (id: string) => void;
}) {
  const [adding, setAdding] = useState(false);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [confirmId, setConfirmId] = useState<string | null>(null);

  return (
    <section className="settings-section">
      <h3>MCP Servers</h3>
      {servers.length === 0 && <p className="settings-hint">No servers configured.</p>}

      {servers.map((s) =>
        editingId === s.id ? (
          <McpForm
            key={s.id}
            initial={s}
            onCancel={() => setEditingId(null)}
            onSubmit={(srv, cred) => {
              onSave(srv, cred);
              setEditingId(null);
            }}
          />
        ) : (
          <div key={s.id} className="mcp-item">
            <label className="mcp-toggle" title={s.enabled ? "Enabled" : "Disabled"}>
              <input
                type="checkbox"
                checked={s.enabled}
                onChange={(e) => onSave({ ...s, enabled: e.currentTarget.checked })}
              />
            </label>
            <div className="mcp-main">
              <div className="mcp-name">{s.name || s.id}</div>
              <div className="mcp-sub">
                {s.transport.kind === "http"
                  ? `http · ${s.transport.url ?? "—"}${s.httpCredentialSet ? " · 🔑" : ""}`
                  : `stdio · ${s.transport.command ?? "—"} ${(s.transport.args ?? []).join(" ")}`}
              </div>
            </div>
            <select
              className="mcp-policy"
              value={s.toolPolicy ?? "auto"}
              title="Tool policy"
              onChange={(e) => onSave({ ...s, toolPolicy: e.currentTarget.value })}
            >
              <option value="auto">auto</option>
              <option value="confirm-destructive">confirm-destructive</option>
              <option value="confirm-all">confirm-all</option>
            </select>
            <button className="btn-mini" onClick={() => setEditingId(s.id)}>
              edit
            </button>
            {confirmId === s.id ? (
              <span className="mcp-confirm">
                <button
                  className="btn-danger sm"
                  onClick={() => {
                    onDelete(s.id);
                    setConfirmId(null);
                  }}
                >
                  delete
                </button>
                <button className="btn-mini" onClick={() => setConfirmId(null)}>
                  ✕
                </button>
              </span>
            ) : (
              <button className="btn-mini danger" onClick={() => setConfirmId(s.id)}>
                ✕
              </button>
            )}
          </div>
        ),
      )}

      {adding ? (
        <McpForm
          onCancel={() => setAdding(false)}
          onSubmit={(srv, cred) => {
            onSave(srv, cred);
            setAdding(false);
          }}
        />
      ) : (
        <button className="btn-secondary" onClick={() => setAdding(true)}>
          + Add MCP server
        </button>
      )}
      <p className="settings-hint">
        HTTP credentials are stored in the keychain by server id — never in the config file or shown here.
        Changes reconnect servers live.
      </p>
    </section>
  );
}

// Add/edit form for one MCP server. Preserves the existing stdio `env` (edited via
// the file, not surfaced here) so toggles/edits don't wipe it.
function McpForm({
  initial,
  onSubmit,
  onCancel,
}: {
  initial?: McpServerView;
  onSubmit: (server: McpServerView, httpCredential?: string) => void;
  onCancel: () => void;
}) {
  const [id, setId] = useState(initial?.id ?? "");
  const [name, setName] = useState(initial?.name ?? "");
  const [kind, setKind] = useState<"stdio" | "http">(initial?.transport.kind ?? "stdio");
  const [command, setCommand] = useState(initial?.transport.command ?? "");
  const [args, setArgs] = useState((initial?.transport.args ?? []).join(" "));
  const [url, setUrl] = useState(initial?.transport.url ?? "");
  const [cred, setCred] = useState("");
  const editing = !!initial;

  const valid = id.trim() && (kind === "stdio" ? command.trim() : url.trim());

  const submit = () => {
    const server: McpServerView = {
      id: id.trim(),
      name: name.trim() || id.trim(),
      enabled: initial?.enabled ?? true,
      toolPolicy: initial?.toolPolicy,
      transport:
        kind === "stdio"
          ? {
              kind: "stdio",
              command: command.trim(),
              args: args.trim() ? args.trim().split(/\s+/) : [],
              env: initial?.transport.env, // preserved, edited via the file
            }
          : { kind: "http", url: url.trim() },
      httpCredentialSet: initial?.httpCredentialSet,
    };
    onSubmit(server, cred.trim() || undefined);
  };

  return (
    <div className="mcp-form">
      <div className="mcp-form-row">
        <input
          className="settings-input"
          placeholder="id (e.g. github)"
          value={id}
          disabled={editing}
          onChange={(e) => setId(e.currentTarget.value)}
        />
        <input
          className="settings-input"
          placeholder="name"
          value={name}
          onChange={(e) => setName(e.currentTarget.value)}
        />
        <select value={kind} onChange={(e) => setKind(e.currentTarget.value as "stdio" | "http")}>
          <option value="stdio">stdio</option>
          <option value="http">http</option>
        </select>
      </div>

      {kind === "stdio" ? (
        <div className="mcp-form-row">
          <input
            className="settings-input"
            placeholder="command (e.g. npx)"
            value={command}
            onChange={(e) => setCommand(e.currentTarget.value)}
          />
          <input
            className="settings-input grow"
            placeholder="args (space-separated)"
            value={args}
            onChange={(e) => setArgs(e.currentTarget.value)}
          />
        </div>
      ) : (
        <div className="mcp-form-row">
          <input
            className="settings-input grow"
            placeholder="https://… (server URL)"
            value={url}
            onChange={(e) => setUrl(e.currentTarget.value)}
          />
          <input
            type="password"
            className="settings-input"
            placeholder={initial?.httpCredentialSet ? "replace credential…" : "bearer credential (optional)"}
            value={cred}
            onChange={(e) => setCred(e.currentTarget.value)}
            autoComplete="off"
          />
        </div>
      )}

      <div className="mcp-form-actions">
        <button className="btn-mini" onClick={onCancel}>
          cancel
        </button>
        <button className="btn-primary" disabled={!valid} onClick={submit}>
          {editing ? "Save" : "Add"}
        </button>
      </div>
    </div>
  );
}
