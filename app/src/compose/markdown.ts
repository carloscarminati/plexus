import type { Block } from "../contract";

// X0 — Block → Markdown serialization. A per-block-type mapping so X1/X2 export
// targets (PDF, slides, …) can follow the same shape; deliberately NOT a target
// abstraction framework yet. Pure functions — no DOM, no contract/sidecar change.

export function blocksToMarkdown(blocks: Block[]): string {
  const body = blocks.map(blockToMarkdown).join("\n\n").trim();
  return body.length > 0 ? body + "\n" : "";
}

export function blockToMarkdown(block: Block): string {
  switch (block.type) {
    case "markdown":
      return block.text.trim();
    case "table":
      return tableToMarkdown(
        block.columns.map((c) => c.label),
        block.columns.map((c) => c.align),
        block.rows.map((r) => block.columns.map((c) => r[c.key])),
        block.caption,
      );
    case "code":
      return "```" + (block.language ?? "") + "\n" + block.code + "\n```";
    case "link_card": {
      const link = `[${block.title ?? block.url}](${block.url})`;
      return block.description ? `${link}\n\n${block.description}` : link;
    }
    case "choices": {
      const head = block.prompt ? `${block.prompt}\n\n` : "";
      return head + block.options.map((o) => `- ${o.label}`).join("\n");
    }
    case "chart":
      return chartToMarkdown(block);
    default:
      // mcp_ui and any unknown/future block — a graceful placeholder, never a crash.
      return placeholder(block);
  }
}

function cell(value: unknown): string {
  if (value === null || value === undefined) return "";
  return String(value).replace(/\|/g, "\\|").replace(/\r?\n/g, " ");
}

function alignBar(align?: string): string {
  if (align === "center") return ":---:";
  if (align === "right") return "---:";
  return "---";
}

function tableToMarkdown(headers: string[], aligns: (string | undefined)[], rows: unknown[][], caption?: string): string {
  if (headers.length === 0) return caption ? `*${caption}*` : "";
  const head = `| ${headers.map(cell).join(" | ")} |`;
  const sep = `| ${headers.map((_, i) => alignBar(aligns[i])).join(" | ")} |`;
  const body = rows.map((r) => `| ${headers.map((_, i) => cell(r[i])).join(" | ")} |`).join("\n");
  const cap = caption ? `\n\n*${caption}*` : "";
  return `${head}\n${sep}${body ? "\n" + body : ""}${cap}`;
}

// Chart → its title as a heading + its data records as a Markdown table (the
// data-table fallback; image embedding is X1/PDF).
function chartToMarkdown(block: Extract<Block, { type: "chart" }>): string {
  const heading = `### ${block.title ?? "Chart"}`;
  const data = block.data ?? [];
  if (data.length === 0) return `${heading}\n\n_(no data)_`;
  const keys: string[] = [];
  for (const rec of data) for (const k of Object.keys(rec)) if (!keys.includes(k)) keys.push(k);
  const table = tableToMarkdown(
    keys,
    keys.map(() => undefined),
    data.map((rec) => keys.map((k) => rec[k])),
  );
  return `${heading}\n\n${table}`;
}

function placeholder(block: Block): string {
  const type = (block as { type?: string }).type ?? "unknown";
  if (type === "mcp_ui") return "> _[interactive mcp_ui block — omitted from export]_";
  return `> _[${type} block — omitted from export]_`;
}

// Save Markdown to a file. X0 uses a browser download (pure-frontend); a native
// Tauri save dialog is an X1 upgrade (needs the dialog/fs plugins + capability).
export function saveMarkdown(filename: string, content: string): void {
  const blob = new Blob([content], { type: "text/markdown;charset=utf-8" });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 0);
}

export function deliverableFilename(title?: string): string {
  const slug = (title ?? "").toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 40);
  return (slug || "deliverable") + ".md";
}
