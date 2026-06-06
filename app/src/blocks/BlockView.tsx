import { marked } from "marked";
import type { Block } from "../contract";

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
  return (
    <div className="block block-code">
      <div className="code-header">
        <span>{block.filename ?? block.language}</span>
        <button onClick={() => navigator.clipboard?.writeText(block.code)}>copy</button>
      </div>
      <pre>
        <code>{block.code}</code>
      </pre>
    </div>
  );
}

// Dependency-free SVG chart for line / bar / scatter.
function ChartView({ block }: { block: Extract<Block, { type: "chart" }> }) {
  const W = 480;
  const H = 240;
  const pad = { top: 16, right: 16, bottom: 36, left: 40 };
  const plotW = W - pad.left - pad.right;
  const plotH = H - pad.top - pad.bottom;

  const series = block.series ?? [];
  const all = series.flatMap((s) => s.values);
  const max = Math.max(0, ...all);
  const min = Math.min(0, ...all);
  const span = max - min || 1;
  const n = Math.max(1, ...series.map((s) => s.values.length));
  const colors = ["#6ea8fe", "#6ee7a8", "#f0c674", "#f08a8a", "#c792ea"];

  const x = (i: number) => pad.left + (n === 1 ? plotW / 2 : (i / (n - 1)) * plotW);
  const y = (v: number) => pad.top + plotH - ((v - min) / span) * plotH;
  const xBand = plotW / n;

  return (
    <div className="block block-chart">
      <svg viewBox={`0 0 ${W} ${H}`} width="100%" role="img">
        {/* axes */}
        <line x1={pad.left} y1={pad.top} x2={pad.left} y2={pad.top + plotH} className="axis" />
        <line x1={pad.left} y1={pad.top + plotH} x2={pad.left + plotW} y2={pad.top + plotH} className="axis" />
        {/* zero baseline if data crosses zero */}
        {min < 0 && max > 0 && (
          <line x1={pad.left} y1={y(0)} x2={pad.left + plotW} y2={y(0)} className="axis-zero" />
        )}

        {series.map((s, si) => {
          const color = colors[si % colors.length];
          if (block.chart === "bar") {
            const groupW = xBand * 0.8;
            const barW = groupW / series.length;
            return s.values.map((v, i) => (
              <rect
                key={`${si}-${i}`}
                x={pad.left + i * xBand + (xBand - groupW) / 2 + si * barW}
                y={Math.min(y(v), y(0))}
                width={Math.max(1, barW - 2)}
                height={Math.abs(y(v) - y(0))}
                fill={color}
              />
            ));
          }
          if (block.chart === "scatter") {
            return s.values.map((v, i) => (
              <circle key={`${si}-${i}`} cx={x(i)} cy={y(v)} r={4} fill={color} />
            ));
          }
          // line
          const d = s.values.map((v, i) => `${i === 0 ? "M" : "L"}${x(i)},${y(v)}`).join(" ");
          return <path key={si} d={d} fill="none" stroke={color} strokeWidth={2} />;
        })}

        {/* x labels */}
        {block.xLabels?.map((label, i) => (
          <text
            key={i}
            x={block.chart === "bar" ? pad.left + i * xBand + xBand / 2 : x(i)}
            y={pad.top + plotH + 16}
            className="axis-label"
            textAnchor="middle"
          >
            {label}
          </text>
        ))}
      </svg>
      {(block.series.length > 1 || block.series[0]?.name) && (
        <div className="chart-legend">
          {block.series.map((s, i) => (
            <span key={i}>
              <i style={{ background: colors[i % colors.length] }} />
              {s.name ?? `Series ${i + 1}`}
            </span>
          ))}
        </div>
      )}
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
