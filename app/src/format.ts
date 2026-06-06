// Display helpers shared by the canvas cards and the detail pane.

export function formatCost(usd: number): string {
  if (usd === 0) return "$0";
  if (usd < 0.01) return `$${usd.toFixed(4)}`;
  return `$${usd.toFixed(3)}`;
}

// "claude-opus-4-8" -> "opus-4-8"; strips the provider-ish prefix for a compact badge.
export function shortModel(modelId: string): string {
  return modelId.replace(/^claude-/, "").replace(/-\d{8}$/, "");
}
