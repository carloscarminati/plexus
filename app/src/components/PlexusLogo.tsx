// The Plexus mark as inline SVG so the app's design tokens cascade into it
// (theme-aware) — never hardcoded hex. Geometry mirrors app/src/assets/plexus-logo.svg
// (one source → three → one accent sink). The accent node uses the SAME token as the
// deliverable node (--accent-brief), so the convergence point reads as the brief violet.
export function PlexusLogo({
  size = 28,
  variant = "mark",
  title = "Plexus",
}: {
  size?: number;
  variant?: "mark" | "icon"; // 'mark' = bare/transparent; 'icon' = rounded-square bg
  title?: string;
}) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 512 512"
      role="img"
      aria-label={title}
      xmlns="http://www.w3.org/2000/svg"
    >
      <title>{title}</title>
      {variant === "icon" && <rect x="0" y="0" width="512" height="512" rx="112" fill="var(--bg)" />}
      <g stroke="var(--muted)" strokeWidth={9} strokeLinecap="round">
        <line x1="140" y1="256" x2="248" y2="172" />
        <line x1="140" y1="256" x2="248" y2="256" />
        <line x1="140" y1="256" x2="248" y2="340" />
        <line x1="248" y1="172" x2="356" y2="256" />
        <line x1="248" y1="256" x2="356" y2="256" />
        <line x1="248" y1="340" x2="356" y2="256" />
      </g>
      <circle cx="140" cy="256" r="24" fill="var(--muted)" />
      <circle cx="248" cy="172" r="24" fill="var(--muted)" />
      <circle cx="248" cy="256" r="24" fill="var(--muted)" />
      <circle cx="248" cy="340" r="24" fill="var(--muted)" />
      <circle cx="356" cy="256" r="36" fill="var(--accent-brief)" />
    </svg>
  );
}
