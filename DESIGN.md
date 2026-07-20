# DESIGN.md — LabConnect Design System

- Status: Source of truth (living document), derived from the `/ui-ux-pro-max`
  workflow (design-system generation + domain refinement). Machine-generated
  baseline persisted at `design-system/labconnect/MASTER.md`; this file is the
  curated, binding version.
- Character: **precise, calm, trustworthy, operationally confident** — built for
  laboratory operations. Explicitly *not* a generic AI SaaS dashboard.

## 1. Direction (from /ui-ux-pro-max, refined)

- **App shell style:** Data-Dense Dashboard (BI/operations family) — efficient
  grids, KPI rows, rich sortable tables, minimal padding (8–12px), sticky
  headers, maximum signal per screen. Dials: variance 3 (centered/minimal),
  motion 2 (subtle), density 8 (dashboard).
- **Public pages pattern:** Real-Time / Operations landing — product + live
  status preview, key metrics, how-it-works, trust signals, restrained CTA.
- **Anti-patterns (binding):** no AI purple/pink gradients, no glassmorphism,
  no decorative animation, no oversized radii, no nested cards, no emoji icons,
  no playful styling, no hidden credentials UX.

## 2. Color system (OKLCH tokens; hex fallbacks)

Neutral surfaces carry the interface; clinical teal is the identity accent;
status colors are never the only signal (always icon + text).

| Token | Light | Dark | Role |
|---|---|---|---|
| `--surface-0` | `oklch(0.99 0.002 220)` ≈ #FBFCFD | `oklch(0.16 0.01 230)` | page background |
| `--surface-1` | `#FFFFFF` | `oklch(0.20 0.012 230)` | cards, panels, table rows |
| `--surface-2` | `oklch(0.96 0.004 220)` ≈ #F1F4F6 | `oklch(0.24 0.014 230)` | muted fills, headers |
| `--border` | `oklch(0.90 0.006 220)` ≈ #DDE3E8 | `oklch(0.32 0.014 230)` | hairlines, dividers |
| `--fg` | `oklch(0.25 0.02 240)` ≈ #1C2B36 | `oklch(0.93 0.008 220)` | primary text |
| `--fg-muted` | `oklch(0.50 0.02 240)` ≈ #5A6B78 | `oklch(0.70 0.012 220)` | secondary text |
| `--primary` | `oklch(0.55 0.11 215)` ≈ #0E7490 | `oklch(0.70 0.11 215)` | brand teal, primary actions, focus ring |
| `--primary-fg` | `#FFFFFF` | `oklch(0.16 0.01 230)` | on-primary |
| `--ok` | `oklch(0.55 0.13 155)` ≈ #047857 | lighter tonal | online/success |
| `--warn` | `oklch(0.70 0.14 70)` ≈ #B45309 | lighter tonal | warning/stale |
| `--danger` | `oklch(0.55 0.19 25)` ≈ #DC2626 | lighter tonal | error/destructive |
| `--info` | `oklch(0.58 0.10 250)` ≈ #1D4ED8 | lighter tonal | informational |

Rules: text contrast ≥ 4.5:1 (verify per mode, both themes designed together);
status = color + icon + label; semantic tokens only in components (no raw hex);
dark mode is tonal, not inverted.

## 3. Typography

- **Family:** Plus Jakarta Sans (headings 600/700, body 400, labels 500),
  self-hosted with `font-display: swap`; tabular figures (`font-variant-numeric:
  tabular-nums`) for all data columns, counts, and timers.
- **Scale:** 12 (dense meta) · 13 (table body) · 14 (body) · 16 (base) ·
  18 (section) · 24 (page title) · 32 (public headings). Line-height 1.5 body,
  1.3 headings. Monospace (`JetBrains Mono`) for ids, hashes, raw frames.

## 4. Spacing, layout, radii, elevation

- Spacing scale (density 8/10): 4 / 8 / 12 / 16 / 24 / 32 (px). Table row 36px;
  header 56px; sidebar 240px (collapsible to 64px icon rail).
- Radii: 4px controls · 6px cards/drawers · 999px pills/badges. Nothing larger.
- Elevation: 3 levels only — flat (borders do the work), raised (subtle
  `0 1px 2px` for sticky headers/menus), overlay (drawers/dialogs with 40–60%
  scrim). No decorative shadows.
- Breakpoints: 375 / 768 / 1024 / 1440. Desktop-first for operational screens;
  tables collapse to keyed card lists on <768px; no horizontal page scroll.

## 5. Component vocabulary (binding patterns)

- **Drawers** (right, 480/720px) for create/edit/device operations — never
  navigate away for contextual work. Unsaved-change confirm on dismiss.
- **Dialogs** only for short focused confirmations (destructive actions require
  typed/checked confirmation; danger button separated).
- **Popovers** for lightweight controls (column pickers, filters, row actions).
- **Tables:** sorting (aria-sort), filtering, saved views, column controls,
  pagination or virtualization >50 rows, sticky header, keyboard row focus,
  bulk-select with safe bulk actions, per-cell truncation with tooltip, empty /
  loading-skeleton / error / permission-denied states.
- **Status badges:** pill + icon + text (`online`, `offline`, `never seen`,
  `decommissioned`, `capture-only`, `validating`, `approved`, `production`,
  `suspended`). Color-independent meaning.
- **Forms:** visible labels, helper text, inline validation on blur, error below
  field + summary with anchors, focus first invalid, semantic input types,
  masked secrets with reveal-and-audit, test actions where configurable.
- **Every interactive control** defines: default, hover, focus (2px ring
  `--primary`, offset 2px), active, selected, disabled (0.45 opacity + cursor),
  loading (inline spinner, width-stable), error, success.

## 6. Motion

Subtle tier (motion 2/10): 150–250ms, `ease-out` enter / `ease-in` exit; route
transitions ≤200ms fade; drawer slide 200ms; no parallax, no stagger theatrics;
`prefers-reduced-motion` disables all non-essential motion. Animation never
blocks input.

## 7. Iconography

**Lucide** exclusively — 1.5px stroke, 16/20/24px tokens, outline style only,
`aria-label` on icon-only buttons, no emoji as UI, official brand assets only
for third parties.

## 8. Accessibility (WCAG 2.2 AA, binding)

Keyboard-complete (visible focus everywhere, logical tab order, skip-link);
screen-reader labels/roles/live-regions (toasts `aria-live=polite`, errors
`role=alert`); 44px touch targets on touch surfaces; text scaling to 200%
without loss; focus moved to main on route change; color-independent status;
reduced-motion honored; both themes contrast-verified independently.

## 9. Internationalization

English first; all strings externalized; layout uses logical properties
(`margin-inline-start`, not `-left`) and flex/grid direction awareness for
**Arabic/RTL readiness**; dates/numbers locale-formatted (UTC stored, local
displayed with zone indicator); RTL smoke-test in visual QA.

## 10. Visual QA process

Every critical route is screenshotted at 375 / 768 / 1440 in light + dark,
inspected against this document, and checked with automated accessibility
tooling before a phase exits. Screenshots are part of phase evidence.

## 11. Implementation home

Tokens + primitives live in `packages/ui` (extended from the existing scaffold):
`tokens.ts` (OKLCH), `primitives/` (Button, Input, Select, Badge, Drawer,
Dialog, Popover, Table, Tabs, Toast, EmptyState, Skeleton), consumed by
`apps/control-plane-web` and `apps/gateway-desktop`. Per-page deviations are
documented in `design-system/labconnect/pages/*.md` and override this file only
where explicitly stated.
