import { useEffect, useRef, useState, type FormEvent } from "react";
import { BlockView } from "./blocks/BlockView";
import { useSidecar } from "./useSidecar";
import "./App.css";

function App() {
  const { status, graph, pending, error, sendMessage } = useSidecar();
  const [draft, setDraft] = useState("");
  const scrollRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight, behavior: "smooth" });
  }, [graph?.nodes.length, pending]);

  const doSend = () => {
    if (!draft.trim() || pending) return;
    sendMessage(draft);
    setDraft("");
  };

  const submit = (e: FormEvent) => {
    e.preventDefault();
    doSend();
  };

  const nodes = graph?.nodes ?? [];

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">Plexus</div>
        <div className={`status status-${status}`}>{status}</div>
      </header>

      <div className="conversation" ref={scrollRef}>
        {nodes.length === 0 && !pending && (
          <div className="empty">Ask anything — answers render as typed blocks.</div>
        )}

        {nodes.map((node) => (
          <div key={node.id} className={`turn turn-${node.role}`}>
            <div className="turn-role">{node.role}</div>
            <div className="turn-body">
              {node.blocks.map((block, i) => (
                <BlockView key={i} block={block} />
              ))}
            </div>
            {node.meta?.tokensOut != null && (
              <div className="turn-meta">
                {node.meta.model} · {node.meta.tokensIn ?? "?"}→{node.meta.tokensOut} tok
              </div>
            )}
          </div>
        ))}

        {pending && (
          <div className="turn turn-assistant">
            <div className="turn-role">assistant</div>
            <div className="turn-body">
              <div className="thinking">
                <span></span>
                <span></span>
                <span></span>
              </div>
            </div>
          </div>
        )}
      </div>

      {error && <div className="error-bar">{error}</div>}

      <form className="composer" onSubmit={submit}>
        <textarea
          value={draft}
          onChange={(e) => setDraft(e.currentTarget.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter" && !e.shiftKey) {
              e.preventDefault();
              doSend();
            }
          }}
          placeholder={status === "online" ? "Send a message…" : "Connecting to sidecar…"}
          disabled={status !== "online"}
          rows={2}
        />
        <button type="submit" disabled={status !== "online" || !!pending || !draft.trim()}>
          Send
        </button>
      </form>
    </div>
  );
}

export default App;
