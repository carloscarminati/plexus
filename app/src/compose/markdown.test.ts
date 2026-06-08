import { describe, it, expect } from "vitest";
import { blockToMarkdown, blocksToMarkdown, deliverableFilename } from "./markdown";
import type { Block } from "../contract";

describe("blockToMarkdown — one mapping per block type", () => {
  it("markdown passes through", () => {
    expect(blockToMarkdown({ type: "markdown", text: "# Hi\n\ntext" })).toBe("# Hi\n\ntext");
  });

  it("table → Markdown table with alignment", () => {
    const md = blockToMarkdown({
      type: "table",
      columns: [
        { key: "a", label: "A" },
        { key: "b", label: "B", align: "right" },
      ],
      rows: [{ a: "x", b: 1 }, { a: "y", b: 2 }],
    });
    expect(md).toContain("| A | B |");
    expect(md).toContain("| --- | ---: |");
    expect(md).toContain("| x | 1 |");
    expect(md).toContain("| y | 2 |");
  });

  it("code → fenced block with its language", () => {
    expect(blockToMarkdown({ type: "code", language: "python", code: "print(1)" })).toBe(
      "```python\nprint(1)\n```",
    );
  });

  it("link_card → link + description", () => {
    expect(
      blockToMarkdown({ type: "link_card", url: "https://x.com", title: "X", description: "d" }),
    ).toBe("[X](https://x.com)\n\nd");
    // no description, no title → uses the url as the link text
    expect(blockToMarkdown({ type: "link_card", url: "https://y.com" })).toBe("[https://y.com](https://y.com)");
  });

  it("choices → Markdown list (with optional prompt)", () => {
    const md = blockToMarkdown({
      type: "choices",
      prompt: "Pick one",
      options: [{ id: "a", label: "Alpha" }, { id: "b", label: "Beta" }],
    });
    expect(md).toBe("Pick one\n\n- Alpha\n- Beta");
  });

  it("chart → title heading + data-table fallback", () => {
    const md = blockToMarkdown({
      type: "chart",
      mark: "bar",
      title: "Sales",
      data: [{ region: "N", sales: 120 }, { region: "S", sales: 90 }],
      encoding: { x: { field: "region" }, y: { field: "sales" } },
    });
    expect(md).toContain("### Sales");
    expect(md).toContain("| region | sales |");
    expect(md).toContain("| N | 120 |");
    expect(md).toContain("| S | 90 |");
  });

  it("chart with no data → heading + (no data), never crashes", () => {
    const md = blockToMarkdown({ type: "chart", mark: "arc", data: [], encoding: {} });
    expect(md).toContain("### Chart");
    expect(md).toContain("_(no data)_");
  });

  it("mcp_ui → graceful placeholder", () => {
    const md = blockToMarkdown({ type: "mcp_ui", resourceUri: "ui://x", mimeType: "text/html" });
    expect(md).toContain("mcp_ui");
    expect(md).toMatch(/omitted/i);
  });

  it("unknown/future block → graceful placeholder, never a crash", () => {
    const md = blockToMarkdown({ type: "future_widget", whatever: 1 } as unknown as Block);
    expect(md).toContain("future_widget");
    expect(md).toMatch(/omitted/i);
  });
});

describe("blocksToMarkdown", () => {
  it("empty selection → empty string (no crash)", () => {
    expect(blocksToMarkdown([])).toBe("");
  });

  it("joins multiple blocks with blank lines and a trailing newline", () => {
    const md = blocksToMarkdown([
      { type: "markdown", text: "one" },
      { type: "code", language: "js", code: "x=1" },
    ]);
    expect(md).toBe("one\n\n```js\nx=1\n```\n");
  });

  it("an unknown block among valid ones doesn't break the document", () => {
    const md = blocksToMarkdown([
      { type: "markdown", text: "intro" },
      { type: "mystery" } as unknown as Block,
      { type: "markdown", text: "outro" },
    ]);
    expect(md).toContain("intro");
    expect(md).toContain("outro");
    expect(md).toContain("mystery");
  });
});

describe("deliverableFilename", () => {
  it("slugs the graph title", () => {
    expect(deliverableFilename("REST vs gRPC!")).toBe("rest-vs-grpc.md");
  });
  it("falls back when empty", () => {
    expect(deliverableFilename("")).toBe("deliverable.md");
    expect(deliverableFilename(undefined)).toBe("deliverable.md");
  });
});
