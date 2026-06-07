import { useEffect, useMemo, useRef, useState } from "react";
import type { ChartBlock, ChartChannel } from "../contract";

// The single home of chart visual consistency: the curated chart spec is compiled
// to a THEMED Vega-Lite spec here (app palette / fonts / token colors), then
// rendered with Vega as SVG (crisp under canvas zoom). Vega/Vega-Lite are loaded
// lazily and the chart only renders once it scrolls into view.

const PALETTE = ["#6ea8fe", "#6ee7a8", "#f0c674", "#f08a8a", "#c792ea", "#80cbc4", "#f7a072"];
const FONT = '-apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif';

// Matches the app's design tokens (App.css :root).
const THEME = {
  background: "transparent",
  font: FONT,
  title: { color: "#e6e8ee", fontSize: 13, fontWeight: 600, font: FONT },
  axis: {
    labelColor: "#8b90a0",
    titleColor: "#8b90a0",
    gridColor: "#2a2f3c",
    domainColor: "#2a2f3c",
    tickColor: "#2a2f3c",
    labelFont: FONT,
    titleFont: FONT,
  },
  legend: { labelColor: "#e6e8ee", titleColor: "#8b90a0", labelFont: FONT, titleFont: FONT },
  view: { stroke: "transparent" },
  range: { category: PALETTE, heatmap: ["#1e222d", "#6ea8fe"], ramp: ["#1e222d", "#6ea8fe"] },
};

type FieldType = "quantitative" | "nominal" | "ordinal" | "temporal";

function isNumericField(data: ChartBlock["data"], field: string): boolean {
  const row = data.find((r) => r[field] !== null && r[field] !== undefined);
  return typeof row?.[field] === "number";
}

// Resolve a channel's Vega-Lite type: honor an explicit type, but correct the two
// failure modes seen live — (1) `temporal` on a plain number (e.g. a year integer)
// misparses as an epoch timestamp → use ordinal so ticks render as 2014…2024;
// (2) inferring a line/area x of integers as quantitative yields a broken continuous
// axis → prefer ordinal (discrete sequence). Scatter/point x stays quantitative.
function resolveType(block: ChartBlock, channel: "x" | "y" | "color" | "theta" | "size", field: string, declared?: FieldType): FieldType {
  const numeric = isNumericField(block.data, field);
  if (declared) return declared === "temporal" && numeric ? "ordinal" : declared;
  if (!numeric) return "nominal";
  if (channel === "x" && (block.mark === "line" || block.mark === "area")) return "ordinal";
  return "quantitative";
}

// Curated spec → themed Vega-Lite spec. This is where channel/mark semantics and
// the app's visual identity are applied; everything else is just data.
function toVegaLite(block: ChartBlock, width: number): Record<string, unknown> {
  const encoding: Record<string, Record<string, unknown>> = {};
  (["x", "y", "color", "theta", "size"] as const).forEach((ch) => {
    const c: ChartChannel | undefined = block.encoding[ch];
    if (!c) return;
    encoding[ch] = { field: c.field, type: resolveType(block, ch, c.field, c.type) };
  });

  // Honor record order for categorical x (the curated specs carry inline records,
  // not pre-sorted domains) instead of Vega-Lite's default alphabetical sort.
  if (encoding.x && (encoding.x.type === "nominal" || encoding.x.type === "ordinal")) encoding.x.sort = null;
  if (block.legend === false && encoding.color) encoding.color.legend = null;
  if ((block.mark === "bar" || block.mark === "area") && encoding.y && block.stack !== undefined)
    encoding.y.stack = block.stack ? "zero" : null;

  const mark = block.mark === "point" ? { type: "point", filled: true, size: 60 } : block.mark;

  return {
    $schema: "https://vega.github.io/schema/vega-lite/v5.json",
    data: { values: block.data },
    mark,
    encoding,
    width: Math.max(160, width),
    height: 240,
    autosize: { type: "fit", contains: "padding" },
    ...(block.title ? { title: block.title } : {}),
    config: THEME,
  };
}

export function ChartView({ block }: { block: ChartBlock }) {
  const hostRef = useRef<HTMLDivElement>(null);
  const plotRef = useRef<HTMLDivElement>(null);
  const [visible, setVisible] = useState(false);
  const [error, setError] = useState(false);

  // Memoize the curated→VL transform so an unrelated re-render doesn't rebuild it.
  const specKey = useMemo(() => JSON.stringify(block), [block]);

  // Lazy: only render once the chart scrolls into view.
  useEffect(() => {
    const el = hostRef.current;
    if (!el || visible) return;
    const io = new IntersectionObserver(
      (entries) => {
        if (entries.some((e) => e.isIntersecting)) {
          setVisible(true);
          io.disconnect();
        }
      },
      { rootMargin: "200px" },
    );
    io.observe(el);
    return () => io.disconnect();
  }, [visible]);

  useEffect(() => {
    if (!visible) return;
    let view: { finalize: () => void } | null = null;
    let cancelled = false;

    (async () => {
      try {
        const el = plotRef.current;
        if (!el) return;
        const [vega, vl] = await Promise.all([import("vega"), import("vega-lite")]);
        if (cancelled) return;

        const width = el.clientWidth || 480;
        const compiled = vl.compile(toVegaLite(block, width) as never).spec;

        // Defense in depth: no external resource loading (data is always inline).
        const loader = vega.loader();
        loader.load = () => Promise.reject(new Error("external resource loading is disabled"));

        const v = new vega.View(vega.parse(compiled), {
          renderer: "svg",
          container: el,
          hover: true,
          loader,
        });
        await v.runAsync();
        if (cancelled) {
          v.finalize();
          return;
        }
        view = v;
        setError(false);
      } catch {
        if (!cancelled) setError(true);
      }
    })();

    return () => {
      cancelled = true;
      view?.finalize();
      if (plotRef.current) plotRef.current.innerHTML = "";
    };
  }, [visible, specKey, block]);

  return (
    <div className="block block-chart" ref={hostRef}>
      {error ? (
        <div className="chart-error">Couldn't render this chart.</div>
      ) : (
        <div className="chart-plot" ref={plotRef} />
      )}
    </div>
  );
}
