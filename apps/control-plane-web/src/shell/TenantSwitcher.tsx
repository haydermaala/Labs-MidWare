// Tenant switcher — visible only when the operator belongs to more than one
// laboratory. A single membership renders as a static label (no dead control),
// and no memberships renders nothing.

import { useEffect, useRef, useState } from 'react';
import { color, fontSize, space } from '@lab-connect/ui';
import { useAuth } from '../auth/AuthProvider';

export function TenantSwitcher(): JSX.Element | null {
  const { memberships, activeTenantId, activeRole, selectTenant } = useAuth();
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) {
      return;
    }
    function onPointerDown(e: MouseEvent): void {
      if (ref.current !== null && !ref.current.contains(e.target as Node)) {
        setOpen(false);
      }
    }
    function onKey(e: KeyboardEvent): void {
      if (e.key === 'Escape') {
        setOpen(false);
      }
    }
    document.addEventListener('mousedown', onPointerDown);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onPointerDown);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  if (memberships.length === 0) {
    return null;
  }

  const active = memberships.find((m) => m.tenantId === activeTenantId) ?? memberships[0];
  if (active === undefined) {
    return null;
  }

  const label = (
    <>
      <span className="lc-tenant__name">{active.tenantName}</span>
      <span className="lc-tenant__role">{activeRole ?? active.role}</span>
    </>
  );

  if (memberships.length === 1) {
    return (
      <span className="lc-tenant lc-tenant--static">{label}</span>
    );
  }

  return (
    <div ref={ref} className="lc-tenant__wrap">
      <button
        type="button"
        className="lc-btn lc-btn--ghost lc-tenant"
        aria-haspopup="listbox"
        aria-expanded={open}
        onClick={() => setOpen((v) => !v)}
      >
        {label}
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor"
          strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
          <path d="m6 9 6 6 6-6" />
        </svg>
      </button>

      {open && (
        <ul
          role="listbox"
          aria-label="Switch laboratory"
          className="lc-card"
          style={{
            position: 'absolute', top: '100%', left: 0, marginTop: 4, zIndex: 40,
            minWidth: 260, listStyle: 'none', padding: 4, margin: 0,
            boxShadow: '0 1px 2px rgba(0,0,0,.16)',
          }}
        >
          {memberships.map((m) => (
            <li key={m.tenantId}>
              <button
                type="button"
                role="option"
                aria-selected={m.tenantId === active.tenantId}
                onClick={() => { selectTenant(m.tenantId); setOpen(false); }}
                className="lc-nav-item"
                style={{
                  width: '100%', display: 'grid', gap: 2, textAlign: 'left',
                  padding: `${space[2]}px ${space[3]}px`, borderRadius: 4,
                  border: 'none', cursor: 'pointer',
                  background: m.tenantId === active.tenantId ? color.surface2 : 'transparent',
                  color: color.fg,
                }}
              >
                <span style={{ fontSize: fontSize.body, fontWeight: 500 }}>{m.tenantName}</span>
                <span style={{ fontSize: fontSize.meta, color: color.fgMuted }}>
                  {m.role}{m.tenantActive ? '' : ' · inactive'}
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

/** Tenant switcher styling (truncates rather than overflowing the header). */
export const tenantSwitcherCss = `
.lc-tenant__wrap { position: relative; min-width: 0; }
.lc-tenant {
  display: inline-flex; align-items: center; gap: 8px;
  min-width: 0; font-size: 14px;
}
.lc-tenant--static {
  padding-left: 12px; margin-left: 4px;
  border-left: 1px solid var(--lc-border);
}
.lc-tenant__name {
  font-weight: 600; white-space: nowrap; overflow: hidden;
  text-overflow: ellipsis; max-width: 22ch;
}
.lc-tenant__role { color: var(--lc-fg-muted); font-size: 12px; white-space: nowrap; }
@media (max-width: 767px) {
  .lc-tenant__role { display: none; }
  .lc-tenant__name { max-width: 14ch; }
}
`;
