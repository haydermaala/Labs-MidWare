// LabConnect design tokens — the typed mirror of DESIGN.md §2–4 (binding).
// Semantic tokens only: components never use raw hex. Both themes are designed
// together; dark is tonal, not inverted.

/** CSS custom-property names (single source for TS + CSS). */
export const cssVar = {
  surface0: '--lc-surface-0',
  surface1: '--lc-surface-1',
  surface2: '--lc-surface-2',
  border: '--lc-border',
  fg: '--lc-fg',
  fgMuted: '--lc-fg-muted',
  primary: '--lc-primary',
  primaryFg: '--lc-primary-fg',
  ok: '--lc-ok',
  warn: '--lc-warn',
  danger: '--lc-danger',
  info: '--lc-info',
} as const;

/** var() references for use in styles. */
export const color = Object.fromEntries(
  Object.entries(cssVar).map(([k, v]) => [k, `var(${v})`]),
) as Record<keyof typeof cssVar, string>;

/** Spacing scale (px) — DESIGN.md §4, density 8/10. */
export const space = [0, 4, 8, 12, 16, 24, 32] as const;

/** Radii: controls 4, cards/drawers 6, pills 999. Nothing larger. */
export const radius = { control: 4, card: 6, pill: 999 } as const;

/** Type scale (px) — DESIGN.md §3. */
export const fontSize = {
  meta: 12, table: 13, body: 14, base: 16, section: 18, title: 24, hero: 32,
} as const;

export const fontFamily = {
  sans: "'Plus Jakarta Sans', system-ui, -apple-system, 'Segoe UI', sans-serif",
  mono: "'JetBrains Mono', ui-monospace, SFMono-Regular, Menlo, monospace",
} as const;

/** Layout constants: header 56, sidebar 240/64, table row 36 (DESIGN.md §4). */
export const layout = { header: 56, sidebar: 240, sidebarRail: 64, tableRow: 36 } as const;

/**
 * Legacy token shape (Phase 1 scaffold) — kept so existing screens compile
 * while they migrate to semantic tokens. Values now come from the palette.
 */
export const tokens = {
  color: {
    bg: '#0b0d10',
    fg: '#e6e9ee',
    accent: '#0E7490',
    danger: '#DC2626',
  },
  space,
} as const;

export type Tokens = typeof tokens;
