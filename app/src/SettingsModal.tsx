import { useState } from "react";
import type { AppSettingsView, McpServerView, ModelInfo, RoutingPolicy } from "./contract";
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
  setAnthropicKey,
  deleteAnthropicKey,
  setMcpServer,
  deleteMcpServer,
}: {
  settings: AppSettingsView | null;
  models: ModelInfo[];
  onClose: () => void;
  setGeneralSettings: (confirmTimeoutSeconds: number) => void;
  setDefaultPolicy: (policy: RoutingPolicy) => void;
  setAnthropicKey: (key: string) => void;
  deleteAnthropicKey: () => void;
  setMcpServer: (server: McpServerView, httpCredential?: string) => void;
  deleteMcpServer: (id: string) => void;
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
              configured={settings.anthropicKeyConfigured}
              onSave={setAnthropicKey}
              onRemove={deleteAnthropicKey}
            />
            <RoutingSection value={settings.defaultPolicy} models={models} onChange={setDefaultPolicy} />
            <GeneralSection value={settings.confirmTimeoutSeconds} onSave={setGeneralSettings} />
            <McpSection servers={settings.mcpServers} onSave={setMcpServer} onDelete={deleteMcpServer} />
          </div>
        )}
      </div>
    </div>
  );
}

// ── Providers (Anthropic-only execution) ────────────────────────────────────
function ProvidersSection({
  configured,
  onSave,
  onRemove,
}: {
  configured: boolean;
  onSave: (key: string) => void;
  onRemove: () => void;
}) {
  const [key, setKey] = useState("");
  return (
    <section className="settings-section">
      <h3>Providers</h3>
      <div className="settings-row">
        <div className="settings-label">
          Anthropic API key
          <span className={`key-status ${configured ? "ok" : "missing"}`}>
            {configured ? "✓ configured (keychain)" : "not set"}
          </span>
        </div>
      </div>
      <div className="settings-row">
        <input
          type="password"
          className="settings-input"
          placeholder={configured ? "Enter a new key to replace…" : "sk-ant-…"}
          value={key}
          onChange={(e) => setKey(e.currentTarget.value)}
          autoComplete="off"
        />
        <button
          className="btn-primary"
          disabled={!key.trim()}
          onClick={() => {
            onSave(key.trim());
            setKey("");
          }}
        >
          Save
        </button>
        {configured && (
          <button className="btn-danger" onClick={onRemove}>
            Remove
          </button>
        )}
      </div>
      <p className="settings-hint">
        Stored in the OS keychain, never in a file or shown back here. Execution is Anthropic-only;
        more providers will arrive with multi-provider execution (#1).
      </p>
    </section>
  );
}

// ── Routing default (topbar / per-node override this) ───────────────────────
function RoutingSection({
  value,
  models,
  onChange,
}: {
  value: RoutingPolicy;
  models: ModelInfo[];
  onChange: (p: RoutingPolicy) => void;
}) {
  return (
    <section className="settings-section">
      <h3>Routing</h3>
      <div className="settings-row">
        <span className="settings-label">Default policy for new conversations</span>
        <PolicyPicker value={value} models={models} onChange={(p) => p && onChange(p)} />
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
