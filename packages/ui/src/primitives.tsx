// UI primitives (DESIGN.md §5–6): Button, Field, Badge, Card, Spinner.
// Interaction states (hover/focus/active/disabled/loading) live in
// `componentCss` classes so the full matrix is styled centrally; components
// stay thin. Icons are inline Lucide paths (one icon system, no icon fonts).

import type { ReactNode, ButtonHTMLAttributes, InputHTMLAttributes } from 'react';

export const componentCss = `
.lc-btn {
  display: inline-flex; align-items: center; justify-content: center; gap: 8px;
  height: 32px; padding: 0 12px; border-radius: 4px; border: 1px solid transparent;
  font: 500 14px/1 'Plus Jakarta Sans', system-ui, sans-serif;
  cursor: pointer; user-select: none; transition: background 120ms, border-color 120ms;
}
.lc-btn:disabled { opacity: 0.5; cursor: not-allowed; }
.lc-btn--primary { background: var(--lc-primary); color: var(--lc-primary-fg); }
.lc-btn--primary:hover:not(:disabled) { filter: brightness(1.08); }
.lc-btn--primary:active:not(:disabled) { filter: brightness(0.94); }
.lc-btn--secondary { background: var(--lc-surface-1); color: var(--lc-fg); border-color: var(--lc-border); }
.lc-btn--secondary:hover:not(:disabled) { background: var(--lc-surface-2); }
.lc-btn--danger { background: transparent; color: var(--lc-danger); border-color: var(--lc-danger); }
.lc-btn--danger:hover:not(:disabled) { background: color-mix(in oklch, var(--lc-danger) 10%, transparent); }
.lc-btn--ghost { background: transparent; color: var(--lc-fg-muted); }
.lc-btn--ghost:hover:not(:disabled) { background: var(--lc-surface-2); color: var(--lc-fg); }

.lc-field { display: grid; gap: 4px; }
.lc-field__label { font-size: 12px; font-weight: 500; color: var(--lc-fg-muted); }
.lc-field__error { font-size: 12px; color: var(--lc-danger); }
.lc-field__help { font-size: 12px; color: var(--lc-fg-muted); }
.lc-input {
  height: 32px; padding: 0 10px; border-radius: 4px;
  border: 1px solid var(--lc-border); background: var(--lc-surface-1); color: var(--lc-fg);
  font: 400 14px 'Plus Jakarta Sans', system-ui, sans-serif;
}
.lc-input:hover:not(:disabled) { border-color: var(--lc-fg-muted); }
.lc-input:disabled { opacity: 0.5; cursor: not-allowed; background: var(--lc-surface-2); }
.lc-input[aria-invalid="true"] { border-color: var(--lc-danger); }

.lc-badge {
  display: inline-flex; align-items: center; gap: 4px;
  height: 20px; padding: 0 8px; border-radius: 999px;
  font-size: 12px; font-weight: 500; border: 1px solid transparent; white-space: nowrap;
}
.lc-badge--ok { color: var(--lc-ok); border-color: color-mix(in oklch, var(--lc-ok) 40%, transparent); background: color-mix(in oklch, var(--lc-ok) 10%, transparent); }
.lc-badge--warn { color: var(--lc-warn); border-color: color-mix(in oklch, var(--lc-warn) 40%, transparent); background: color-mix(in oklch, var(--lc-warn) 10%, transparent); }
.lc-badge--danger { color: var(--lc-danger); border-color: color-mix(in oklch, var(--lc-danger) 40%, transparent); background: color-mix(in oklch, var(--lc-danger) 10%, transparent); }
.lc-badge--info { color: var(--lc-info); border-color: color-mix(in oklch, var(--lc-info) 40%, transparent); background: color-mix(in oklch, var(--lc-info) 10%, transparent); }
.lc-badge--neutral { color: var(--lc-fg-muted); border-color: var(--lc-border); background: var(--lc-surface-2); }

.lc-card { background: var(--lc-surface-1); border: 1px solid var(--lc-border); border-radius: 6px; }

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
