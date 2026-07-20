// Theme stylesheet: both themes' custom properties, base reset, elevation scale,
// and the global focus ring. Apps inject this once at startup via
// <style>{themeCss}</style>. Dark mode is tonal (a deep navy, not inverted) — set
// `data-theme="dark"` on <html>; `prefers-color-scheme` is the default signal.
//
// Design language: a calm, confident operations console. Deep navy ink, a single
// crisp blue accent (saturation held in check), and soft tinted elevation give
// depth without ornament. Status is carried by colour + icon, never colour alone.

export const themeCss = `
:root {
  /* Surfaces — cool near-whites with a faint navy cast for depth. */
  --lc-surface-0: oklch(0.984 0.004 245);
  --lc-surface-1: #ffffff;
  --lc-surface-2: oklch(0.966 0.006 245);
  --lc-surface-3: oklch(0.944 0.008 245);
  --lc-border: oklch(0.908 0.008 245);
  --lc-border-strong: oklch(0.852 0.010 245);
  /* Text — deep navy ink, not pure black. */
  --lc-fg: oklch(0.255 0.028 258);
  --lc-fg-muted: oklch(0.475 0.021 258);
  --lc-fg-subtle: oklch(0.605 0.016 258);
  /* Single accent — a confident, readable blue. */
  --lc-primary: oklch(0.520 0.148 250);
  --lc-primary-hover: oklch(0.468 0.150 250);
  --lc-primary-fg: #ffffff;
  --lc-primary-soft: oklch(0.955 0.028 250);
  /* Status. */
  --lc-ok: oklch(0.560 0.140 158);
  --lc-warn: oklch(0.660 0.150 68);
  --lc-danger: oklch(0.545 0.200 25);
  --lc-info: oklch(0.545 0.120 250);
  --lc-ring: var(--lc-primary);
  /* Tinted elevation — shadows carry a trace of the ink hue. */
  --lc-shadow-sm: 0 1px 2px 0 oklch(0.30 0.03 258 / 0.07);
  --lc-shadow-md: 0 1px 2px 0 oklch(0.30 0.03 258 / 0.06), 0 6px 16px -6px oklch(0.30 0.03 258 / 0.16);
  --lc-shadow-lg: 0 2px 6px -2px oklch(0.30 0.03 258 / 0.10), 0 16px 40px -12px oklch(0.30 0.03 258 / 0.26);
  color-scheme: light;
}
@media (prefers-color-scheme: dark) {
  :root:not([data-theme="light"]) {
    --lc-surface-0: oklch(0.170 0.014 258);
    --lc-surface-1: oklch(0.208 0.016 258);
    --lc-surface-2: oklch(0.250 0.018 258);
    --lc-surface-3: oklch(0.290 0.020 258);
    --lc-border: oklch(0.320 0.018 258);
    --lc-border-strong: oklch(0.410 0.020 258);
    --lc-fg: oklch(0.945 0.008 245);
    --lc-fg-muted: oklch(0.720 0.014 245);
    --lc-fg-subtle: oklch(0.600 0.014 245);
    --lc-primary: oklch(0.700 0.135 250);
    --lc-primary-hover: oklch(0.760 0.130 250);
    --lc-primary-fg: oklch(0.170 0.014 258);
    --lc-primary-soft: oklch(0.300 0.055 250);
    --lc-ok: oklch(0.720 0.135 158);
    --lc-warn: oklch(0.780 0.140 68);
    --lc-danger: oklch(0.680 0.185 25);
    --lc-info: oklch(0.720 0.110 250);
    --lc-shadow-sm: 0 1px 2px 0 oklch(0 0 0 / 0.30);
    --lc-shadow-md: 0 1px 2px 0 oklch(0 0 0 / 0.30), 0 8px 20px -6px oklch(0 0 0 / 0.45);
    --lc-shadow-lg: 0 2px 6px -2px oklch(0 0 0 / 0.35), 0 20px 44px -12px oklch(0 0 0 / 0.60);
    color-scheme: dark;
  }
}
:root[data-theme="dark"] {
  --lc-surface-0: oklch(0.170 0.014 258);
  --lc-surface-1: oklch(0.208 0.016 258);
  --lc-surface-2: oklch(0.250 0.018 258);
  --lc-surface-3: oklch(0.290 0.020 258);
  --lc-border: oklch(0.320 0.018 258);
  --lc-border-strong: oklch(0.410 0.020 258);
  --lc-fg: oklch(0.945 0.008 245);
  --lc-fg-muted: oklch(0.720 0.014 245);
  --lc-fg-subtle: oklch(0.600 0.014 245);
  --lc-primary: oklch(0.700 0.135 250);
  --lc-primary-hover: oklch(0.760 0.130 250);
  --lc-primary-fg: oklch(0.170 0.014 258);
  --lc-primary-soft: oklch(0.300 0.055 250);
  --lc-ok: oklch(0.720 0.135 158);
  --lc-warn: oklch(0.780 0.140 68);
  --lc-danger: oklch(0.680 0.185 25);
  --lc-info: oklch(0.720 0.110 250);
  --lc-shadow-sm: 0 1px 2px 0 oklch(0 0 0 / 0.30);
  --lc-shadow-md: 0 1px 2px 0 oklch(0 0 0 / 0.30), 0 8px 20px -6px oklch(0 0 0 / 0.45);
  --lc-shadow-lg: 0 2px 6px -2px oklch(0 0 0 / 0.35), 0 20px 44px -12px oklch(0 0 0 / 0.60);
  color-scheme: dark;
}

*, *::before, *::after { box-sizing: border-box; }
html, body { margin: 0; padding: 0; }
body {
  /* A whisper of depth: the canvas lifts subtly toward the top. */
  background-color: var(--lc-surface-0);
  background-image: radial-gradient(120% 80% at 50% -10%, oklch(0.55 0.10 250 / 0.05), transparent 60%);
  background-attachment: fixed;
  color: var(--lc-fg);
  font-family: 'Plus Jakarta Sans Variable', 'Plus Jakarta Sans', system-ui, -apple-system, 'Segoe UI', sans-serif;
  font-size: 14px;
  line-height: 1.5;
  -webkit-font-smoothing: antialiased;
  text-rendering: optimizeLegibility;
}
h1, h2, h3, h4 { line-height: 1.2; margin: 0; letter-spacing: -0.011em; font-weight: 650; }
code, pre, .lc-mono { font-family: 'JetBrains Mono Variable', 'JetBrains Mono', ui-monospace, SFMono-Regular, Menlo, monospace; }
.lc-tabular { font-variant-numeric: tabular-nums; }

/* Visible focus everywhere (WCAG 2.2 AA); never remove, only style. */
:focus-visible { outline: 2px solid var(--lc-primary); outline-offset: 2px; border-radius: 3px; }

/* Reduced motion is a hard rule: transitions collapse, nothing decorative. */
@media (prefers-reduced-motion: reduce) {
  *, *::before, *::after { animation: none !important; transition: none !important; scroll-behavior: auto !important; }
}
`;
