import { useState } from "react";
import { marked } from "marked";
import type { Block } from "../contract";
import { ChartView } from "./ChartView";

export interface BlockViewProps {
  block: Block;
  // Fired when a `choices` option is clicked; the sidecar decides the next turn.
  onChoice?: (option: { id: string; label: string }) => void;
}

// Dispatches a Block to its renderer. Unknown/future block types fall back to
// their raw JSON so an older client never breaks on a newer block (spec §7).
export function BlockView({ block, onChoice }: BlockViewProps) {
  switch (block.type) {
    case "markdown":
      return <MarkdownView text={block.text} />;
    case "table":
      return <TableView block={block} />;
    case "link_card":
      return <LinkCardView block={block} />;
    case "code":
      return <CodeView block={block} />;
    case "chart":
      return <ChartView block={block} />;
    case "choices":
      return <ChoicesView block={block} onChoice={onChoice} />;
    default:
      return <pre className="block block-unknown">{JSON.stringify(block, null, 2)}</pre>;
  }
}

function MarkdownView({ text }: { text: string }) {
  const html = marked.parse(text, { async: false }) as string;
  return <div className="block block-markdown" dangerouslySetInnerHTML={{ __html: html }} />;
}

function TableView({ block }: { block: Extract<Block, { type: "table" }> }) {
  return (
    <figure className="block block-table">
      <table>
        <thead>
          <tr>
            {block.columns.map((c) => (
              <th key={c.key} style={{ textAlign: c.align ?? "left" }}>
                {c.label}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {block.rows.map((row, i) => (
            <tr key={i}>
              {block.columns.map((c) => (
                <td key={c.key} style={{ textAlign: c.align ?? "left" }}>
                  {formatCell(row[c.key])}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
      {block.caption && <figcaption>{block.caption}</figcaption>}
    </figure>
  );
}

function formatCell(value: string | number | boolean | null | undefined) {
  if (value === null || value === undefined) return "";
  return String(value);
}

function LinkCardView({ block }: { block: Extract<Block, { type: "link_card" }> }) {
  return (
    <a className="block block-linkcard" href={block.url} target="_blank" rel="noreferrer">
      {block.image && (
        <div className="linkcard-image" style={{ backgroundImage: `url(${block.image})` }} />
      )}
      <div className="linkcard-body">
        <div className="linkcard-title">{block.title ?? block.url}</div>
        {block.description && <div className="linkcard-desc">{block.description}</div>}
        <div className="linkcard-url">{hostOf(block.url)}</div>
      </div>
    </a>
  );
}

function hostOf(url: string) {
  try {
    return new URL(url).host;
  } catch {
    return url;
  }
}

function CodeView({ block }: { block: Extract<Block, { type: "code" }> }) {
  const [copied, setCopied] = useState(false);
  const copy = async () => {
    try {
      // Raw code text — not the markdown-fenced form.
      await navigator.clipboard.writeText(block.code);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      /* clipboard unavailable / rejected — no-op */
    }
  };
  return (
    <div className="block block-code">
      <div className="code-header">
        <span>{block.filename ?? block.language}</span>
        <button onClick={copy}>{copied ? "Copied ✓" : "copy"}</button>
      </div>
      <pre>
        <code>{block.code}</code>
      </pre>
    </div>
  );
}

function ChoicesView({
  block,
  onChoice,
}: {
  block: Extract<Block, { type: "choices" }>;
  onChoice?: (option: { id: string; label: string }) => void;
}) {
  return (
    <div className="block block-choices">
      {block.prompt && <div className="choices-prompt">{block.prompt}</div>}
      <div className="choices-options">
        {block.options.map((o) => (
          <button
            key={o.id}
            className="choice"
            disabled={!onChoice}
            onClick={() => onChoice?.(o)}
            title={onChoice ? "Send this as your next message" : undefined}
          >
            {o.label}
          </button>
        ))}
      </div>
    </div>
  );
}
