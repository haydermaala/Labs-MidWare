// Theme stylesheet (DESIGN.md §2, §4, §6): both themes' custom properties, base
// reset, and the global focus ring. Apps inject this once at startup via
// <style>{themeCss}</style> (or a static .css copy). Dark mode is tonal — set
// `data-theme="dark"` on <html>; `prefers-color-scheme` is the default signal.

export const themeCss = `
:root {
  --lc-surface-0: oklch(0.99 0.002 220);
  --lc-surface-1: #ffffff;
  --lc-surface-2: oklch(0.96 0.004 220);
  --lc-border: oklch(0.90 0.006 220);
  --lc-fg: oklch(0.25 0.02 240);
  --lc-fg-muted: oklch(0.50 0.02 240);
  --lc-primary: oklch(0.55 0.11 215);
  --lc-primary-fg: #ffffff;
  --lc-ok: oklch(0.55 0.13 155);
  --lc-warn: oklch(0.70 0.14 70);
  --lc-danger: oklch(0.55 0.19 25);
  --lc-info: oklch(0.58 0.10 250);
  color-scheme: light;
}
@media (prefers-color-scheme: dark) {
  :root:not([data-theme="light"]) {
    --lc-surface-0: oklch(0.16 0.01 230);
    --lc-surface-1: oklch(0.20 0.012 230);
    --lc-surface-2: oklch(0.24 0.014 230);
    --lc-border: oklch(0.32 0.014 230);
    --lc-fg: oklch(0.93 0.008 220);
    --lc-fg-muted: oklch(0.70 0.012 220);
    --lc-primary: oklch(0.70 0.11 215);
    --lc-primary-fg: oklch(0.16 0.01 230);
    --lc-ok: oklch(0.70 0.13 155);
    --lc-warn: oklch(0.78 0.14 70);
    --lc-danger: oklch(0.68 0.19 25);
    --lc-info: oklch(0.72 0.10 250);
    color-scheme: dark;
  }
}
:root[data-theme="dark"] {
  --lc-surface-0: oklch(0.16 0.01 230);
  --lc-surface-1: oklch(0.20 0.012 230);
  --lc-surface-2: oklch(0.24 0.014 230);
  --lc-border: oklch(0.32 0.014 230);
  --lc-fg: oklch(0.93 0.008 220);
  --lc-fg-muted: oklch(0.70 0.012 220);
  --lc-primary: oklch(0.70 0.11 215);
  --lc-primary-fg: oklch(0.16 0.01 230);
  --lc-ok: oklch(0.70 0.13 155);
  --lc-warn: oklch(0.78 0.14 70);
  --lc-danger: oklch(0.68 0.19 25);
  --lc-info: oklch(0.72 0.10 250);
  color-scheme: dark;
}

*, *::before, *::after { box-sizing: border-box; }
html, body { margin: 0; padding: 0; }
body {
  background: var(--lc-surface-0);
  color: var(--lc-fg);
  font-family: 'Plus Jakarta Sans', system-ui, -apple-system, 'Segoe UI', sans-serif;
  font-size: 14px;
  line-height: 1.5;
  -webkit-font-smoothing: antialiased;
}
h1, h2, h3, h4 { line-height: 1.3; margin: 0; }
code, pre, .lc-mono { font-family: 'JetBrains Mono', ui-monospace, SFMono-Regular, Menlo, monospace; }
.lc-tabular { font-variant-numeric: tabular-nums; }

/* Visible focus everywhere (WCAG 2.2 AA); never remove, only style. */
:focus-visible { outline: 2px solid var(--lc-primary); outline-offset: 2px; border-radius: 2px; }

/* Reduced motion is a hard rule: transitions collapse, nothing decorative. */
@media (prefers-reduced-motion: reduce) {
  *, *::before, *::after { animation: none !important; transition: none !important; }
}
`;
