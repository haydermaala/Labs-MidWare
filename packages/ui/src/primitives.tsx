// UI primitives (DESIGN.md §5–6): Button, Field, Badge, Card, Spinner.
// Interaction states (hover/focus/active/disabled/loading) live in
// `componentCss` classes so the full matrix is styled centrally; components
// stay thin. Icons are inline Lucide paths (one icon system, no icon fonts).

import type { ReactNode, ButtonHTMLAttributes, InputHTMLAttributes } from 'react';

export const componentCss = `
.lc-btn {
  display: inline-flex; align-items: center; justify-content: center; gap: 8px;
  height: 36px; padding: 0 15px; border-radius: 8px; border: 1px solid transparent;
  font: 600 13.5px/1 inherit; letter-spacing: -0.005em;
  cursor: pointer; user-select: none;
  transition: background 130ms ease, border-color 130ms ease, box-shadow 130ms ease, transform 90ms ease;
}
.lc-btn:disabled { opacity: 0.5; cursor: not-allowed; }
.lc-btn:active:not(:disabled) { transform: translateY(0.5px); }
.lc-btn--primary { background: var(--lc-primary); color: var(--lc-primary-fg); box-shadow: var(--lc-shadow-sm); }
.lc-btn--primary:hover:not(:disabled) { background: var(--lc-primary-hover); }
.lc-btn--secondary { background: var(--lc-surface-1); color: var(--lc-fg); border-color: var(--lc-border-strong); box-shadow: var(--lc-shadow-sm); }
.lc-btn--secondary:hover:not(:disabled) { background: var(--lc-surface-2); border-color: var(--lc-fg-subtle); }
.lc-btn--danger { background: transparent; color: var(--lc-danger); border-color: color-mix(in oklch, var(--lc-danger) 45%, transparent); }
.lc-btn--danger:hover:not(:disabled) { background: color-mix(in oklch, var(--lc-danger) 12%, transparent); border-color: var(--lc-danger); }
.lc-btn--ghost { background: transparent; color: var(--lc-fg-muted); }
.lc-btn--ghost:hover:not(:disabled) { background: var(--lc-surface-2); color: var(--lc-fg); }

.lc-field { display: grid; gap: 6px; }
.lc-field__label { font-size: 12.5px; font-weight: 600; color: var(--lc-fg); letter-spacing: -0.005em; }
.lc-field__error { font-size: 12px; color: var(--lc-danger); }
.lc-field__help { font-size: 12px; color: var(--lc-fg-muted); }
.lc-input {
  height: 40px; padding: 0 12px; border-radius: 8px;
  border: 1px solid var(--lc-border-strong); background: var(--lc-surface-1); color: var(--lc-fg);
  font: 400 14px inherit; transition: border-color 130ms ease, box-shadow 130ms ease;
}
.lc-input::placeholder { color: var(--lc-fg-subtle); }
.lc-input:hover:not(:disabled) { border-color: var(--lc-fg-subtle); }
.lc-input:focus, .lc-input:focus-visible {
  outline: none; border-color: var(--lc-primary);
  box-shadow: 0 0 0 3px color-mix(in oklch, var(--lc-primary) 20%, transparent);
}
.lc-input:disabled { opacity: 0.55; cursor: not-allowed; background: var(--lc-surface-2); }
.lc-input[aria-invalid="true"] { border-color: var(--lc-danger); }
.lc-input[aria-invalid="true"]:focus { box-shadow: 0 0 0 3px color-mix(in oklch, var(--lc-danger) 20%, transparent); }
select.lc-input { cursor: pointer; padding-right: 8px; }

.lc-badge {
  display: inline-flex; align-items: center; gap: 5px;
  height: 22px; padding: 0 9px; border-radius: 999px;
  font-size: 12px; font-weight: 600; border: 1px solid transparent; white-space: nowrap; letter-spacing: -0.003em;
}
.lc-badge svg { flex: none; }
.lc-badge--ok { color: color-mix(in oklch, var(--lc-ok) 78%, var(--lc-fg)); border-color: color-mix(in oklch, var(--lc-ok) 35%, transparent); background: color-mix(in oklch, var(--lc-ok) 12%, transparent); }
.lc-badge--warn { color: color-mix(in oklch, var(--lc-warn) 72%, var(--lc-fg)); border-color: color-mix(in oklch, var(--lc-warn) 38%, transparent); background: color-mix(in oklch, var(--lc-warn) 14%, transparent); }
.lc-badge--danger { color: var(--lc-danger); border-color: color-mix(in oklch, var(--lc-danger) 38%, transparent); background: color-mix(in oklch, var(--lc-danger) 12%, transparent); }
.lc-badge--info { color: var(--lc-info); border-color: color-mix(in oklch, var(--lc-info) 38%, transparent); background: color-mix(in oklch, var(--lc-info) 12%, transparent); }
.lc-badge--neutral { color: var(--lc-fg-muted); border-color: var(--lc-border); background: var(--lc-surface-2); }

/* Cards — soft elevation on surface-1; add lc-card--hover for interactive cards. */
.lc-card { background: var(--lc-surface-1); border: 1px solid var(--lc-border); border-radius: 12px; box-shadow: var(--lc-shadow-sm); }
.lc-card--hover { transition: box-shadow 150ms ease, border-color 150ms ease, transform 150ms ease; }
.lc-card--hover:hover { box-shadow: var(--lc-shadow-md); border-color: var(--lc-border-strong); }

/* Data table conventions: quiet header, hairline rows, hover highlight. */
.lc-table-wrap { background: var(--lc-surface-1); border: 1px solid var(--lc-border); border-radius: 12px; box-shadow: var(--lc-shadow-sm); overflow: hidden; }
.lc-table-wrap table { border-collapse: collapse; width: 100%; }
.lc-table-wrap thead th { background: var(--lc-surface-2); }
.lc-table-wrap tbody tr { transition: background 120ms ease; }
.lc-table-wrap tbody tr:hover { background: var(--lc-surface-2); }

/* KPI / stat block — big tabular number, quiet label. */
.lc-stat { background: var(--lc-surface-1); border: 1px solid var(--lc-border); border-radius: 12px; box-shadow: var(--lc-shadow-sm); padding: 18px 20px; display: grid; gap: 6px; }
.lc-stat__label { font-size: 12px; font-weight: 600; color: var(--lc-fg-muted); text-transform: uppercase; letter-spacing: 0.04em; }
.lc-stat__value { font-size: 30px; font-weight: 680; line-height: 1; letter-spacing: -0.02em; font-variant-numeric: tabular-nums; }

/* Empty state — composed, with room for an icon, message, and an action. */
.lc-empty {
  display: grid; justify-items: center; gap: 12px; text-align: center;
  padding: 44px 24px; border: 1px dashed var(--lc-border-strong); border-radius: 12px;
  background: var(--lc-surface-1); color: var(--lc-fg-muted);
}
.lc-empty__icon {
  display: grid; place-items: center; width: 44px; height: 44px; border-radius: 999px;
  background: var(--lc-primary-soft); color: var(--lc-primary);
}
.lc-empty__title { font-size: 15px; font-weight: 650; color: var(--lc-fg); }
.lc-empty__body { font-size: 13.5px; max-width: 42ch; line-height: 1.55; }

/* Skeleton shimmer — matches layout dimensions, no blocking spinners. */
@keyframes lc-shimmer { 100% { background-position: -200% 0; } }
.lc-skeleton {
  border-radius: 8px;
  background: linear-gradient(90deg, var(--lc-surface-2) 25%, var(--lc-surface-3) 37%, var(--lc-surface-2) 63%);
  background-size: 200% 100%; animation: lc-shimmer 1.4s ease infinite;
}

@keyframes lc-spin { to { transform: rotate(360deg); } }
.lc-spinner {
  width: 14px; height: 14px; border-radius: 999px;
  border: 2px solid color-mix(in oklch, currentColor 25%, transparent);
  border-top-color: currentColor; animation: lc-spin 700ms linear infinite;
}
`;

type ButtonVariant = 'primary' | 'secondary' | 'danger' | 'ghost';

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  readonly variant?: ButtonVariant;
  readonly loading?: boolean;
}

export function Button({ variant = 'primary', loading = false, disabled, children, ...rest }: ButtonProps): JSX.Element {
  return (
    <button
      type="button"
      className={`lc-btn lc-btn--${variant}`}
      disabled={disabled || loading}
      aria-busy={loading || undefined}
      {...rest}
    >
      {loading && <span className="lc-spinner" aria-hidden="true" />}
      {children}
    </button>
  );
}

export interface FieldProps extends InputHTMLAttributes<HTMLInputElement> {
  readonly label: string;
  readonly error?: string;
  readonly help?: string;
}

let fieldSeq = 0;

/** Labeled input with inline error/help (visible label, error below — DESIGN.md §5). */
export function Field({ label, error, help, id, ...rest }: FieldProps): JSX.Element {
  const inputId = id ?? `lc-field-${++fieldSeq}`;
  const describedBy = error ? `${inputId}-err` : help ? `${inputId}-help` : undefined;
  return (
    <div className="lc-field">
      <label className="lc-field__label" htmlFor={inputId}>{label}</label>
      <input
        id={inputId}
        className="lc-input"
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy}
        {...rest}
      />
      {error && <span id={`${inputId}-err`} className="lc-field__error" role="alert">{error}</span>}
      {!error && help && <span id={`${inputId}-help`} className="lc-field__help">{help}</span>}
    </div>
  );
}

export type StatusKind =
  | 'online' | 'offline' | 'never' | 'decommissioned'
  | 'active' | 'inactive' | 'pending' | 'accepted' | 'revoked' | 'expired';

// Lucide path data (24×24, stroke): one professional icon system, inlined.
const ICON_PATHS: Record<string, string> = {
  check: 'M20 6 9 17l-5-5',
  x: 'M18 6 6 18M6 6l12 12',
  clock: 'M12 6v6l4 2M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20z',
  minus: 'M5 12h14',
  shield: 'M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z',
};

const STATUS_STYLE: Record<StatusKind, { tone: string; icon: string; label: string }> = {
  online: { tone: 'ok', icon: 'check', label: 'online' },
  offline: { tone: 'warn', icon: 'clock', label: 'offline' },
  never: { tone: 'neutral', icon: 'minus', label: 'never seen' },
  decommissioned: { tone: 'danger', icon: 'x', label: 'decommissioned' },
  active: { tone: 'ok', icon: 'check', label: 'active' },
  inactive: { tone: 'danger', icon: 'x', label: 'inactive' },
  pending: { tone: 'info', icon: 'clock', label: 'pending' },
  accepted: { tone: 'ok', icon: 'check', label: 'accepted' },
  revoked: { tone: 'danger', icon: 'x', label: 'revoked' },
  expired: { tone: 'neutral', icon: 'minus', label: 'expired' },
};

/** Status pill: color + icon + text — never color alone (DESIGN.md §2). */
export function StatusBadge({ status }: { readonly status: StatusKind }): JSX.Element {
  const s = STATUS_STYLE[status];
  return (
    <span className={`lc-badge lc-badge--${s.tone}`} role="status">
      <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor"
        strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
        <path d={ICON_PATHS[s.icon]} />
      </svg>
      {s.label}
    </span>
  );
}

export function Card({ children, padding = 16 }: { readonly children: ReactNode; readonly padding?: number }): JSX.Element {
  return <div className="lc-card" style={{ padding }}>{children}</div>;
}
