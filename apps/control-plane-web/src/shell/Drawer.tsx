// Right-side drawer for contextual create/edit operations (DESIGN.md §5).
//
// Focus is trapped while open, Escape closes, the trigger is restored on close,
// and the body scroll is locked. Rendered inline (no portal) which is fine for a
// single-drawer-at-a-time console.

import { useCallback, useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { color, fontSize, space } from '@lab-connect/ui';

export function Drawer({
  open,
  title,
  description,
  onClose,
  children,
}: {
  readonly open: boolean;
  readonly title: string;
  readonly description?: string;
  readonly onClose: () => void;
  readonly children: ReactNode;
}): JSX.Element | null {
  const panelRef = useRef<HTMLDivElement>(null);
  const restoreTo = useRef<HTMLElement | null>(null);

  useEffect(() => {
    if (!open) {
      return;
    }
    restoreTo.current = document.activeElement as HTMLElement | null;
    const previousOverflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';

    // Move focus into the panel.
    const raf = requestAnimationFrame(() => {
      panelRef.current?.querySelector<HTMLElement>(
        'input, select, textarea, button, [href], [tabindex]:not([tabindex="-1"])',
      )?.focus();
    });

    function onKey(e: KeyboardEvent): void {
      if (e.key === 'Escape') {
        e.stopPropagation();
        onClose();
        return;
      }
      if (e.key !== 'Tab' || panelRef.current === null) {
        return;
      }
      // Simple focus trap over the panel's tabbable elements.
      const focusable = Array.from(panelRef.current.querySelectorAll<HTMLElement>(
        'input, select, textarea, button, [href], [tabindex]:not([tabindex="-1"])',
      )).filter((el) => !el.hasAttribute('disabled'));
      if (focusable.length === 0) {
        return;
      }
      const first = focusable[0]!;
      const last = focusable[focusable.length - 1]!;
      if (e.shiftKey && document.activeElement === first) {
        e.preventDefault();
        last.focus();
      } else if (!e.shiftKey && document.activeElement === last) {
        e.preventDefault();
        first.focus();
      }
    }

    document.addEventListener('keydown', onKey, true);
    return () => {
      cancelAnimationFrame(raf);
      document.removeEventListener('keydown', onKey, true);
      document.body.style.overflow = previousOverflow;
      restoreTo.current?.focus();
    };
  }, [open, onClose]);

  if (!open) {
    return null;
  }

  return (
    <div style={{ position: 'fixed', inset: 0, zIndex: 50 }}>
      <button
        type="button"
        aria-label="Close"
        onClick={onClose}
        style={{ position: 'absolute', inset: 0, background: 'rgba(0,0,0,.5)', border: 0, padding: 0, cursor: 'pointer' }}
      />
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-label={title}
        className="lc-drawer"
        style={{
          position: 'absolute', top: 0, right: 0, bottom: 0,
          width: 'min(480px, 100vw)', background: color.surface1,
          borderLeft: `1px solid ${color.border}`,
          display: 'grid', gridTemplateRows: 'auto 1fr', boxShadow: '0 0 24px rgba(0,0,0,.24)',
        }}
      >
        <header style={{
          padding: space[4], borderBottom: `1px solid ${color.border}`,
          display: 'flex', alignItems: 'start', gap: space[3],
        }}>
          <div style={{ display: 'grid', gap: 4, flex: 1, minWidth: 0 }}>
            <h2 style={{ fontSize: fontSize.section, fontWeight: 600 }}>{title}</h2>
            {description !== undefined && (
              <p style={{ margin: 0, fontSize: fontSize.body, color: color.fgMuted }}>{description}</p>
            )}
          </div>
          <button type="button" className="lc-btn lc-btn--ghost" aria-label="Close" onClick={onClose} style={{ padding: 6 }}>
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor"
              strokeWidth="2" strokeLinecap="round" aria-hidden="true"><path d="M18 6 6 18M6 6l12 12" /></svg>
          </button>
        </header>
        <div style={{ padding: space[4], overflowY: 'auto' }}>{children}</div>
      </div>
    </div>
  );
}

/** Copy-to-clipboard field for one-time secrets (enrollment tokens, links). */
export function CopyField({ label, value, help }: {
  readonly label: string; readonly value: string; readonly help?: string;
}): JSX.Element {
  const [copied, setCopied] = useCopied();
  return (
    <div className="lc-field">
      <span className="lc-field__label">{label}</span>
      <div style={{ display: 'flex', gap: space[2] }}>
        <input className="lc-input lc-mono" readOnly value={value} style={{ flex: 1, fontSize: 12 }}
          onFocus={(e) => e.currentTarget.select()} />
        <button
          type="button"
          className="lc-btn lc-btn--secondary"
          onClick={() => { void navigator.clipboard?.writeText(value); setCopied(); }}
        >
          {copied ? 'Copied' : 'Copy'}
        </button>
      </div>
      {help !== undefined && <span className="lc-field__help">{help}</span>}
    </div>
  );
}

function useCopied(): [boolean, () => void] {
  const [copied, setCopied] = useState(false);
  const mark = useCallback(() => {
    setCopied(true);
    window.setTimeout(() => setCopied(false), 1500);
  }, []);
  return [copied, mark];
}
