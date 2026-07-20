// Shared chrome for the public (signed-out) pages: a slim marketing header with
// product mark + primary nav, and a footer with legal/status links. Calm and
// restrained per DESIGN.md — no marketing gimmicks; this is infrastructure.

import type { ReactNode } from 'react';
import { Link, NavLink } from 'react-router-dom';
import { color, fontSize, space } from '@lab-connect/ui';

const NAV = [
  { to: '/pricing', label: 'Pricing' },
  { to: '/security', label: 'Security' },
  { to: '/docs', label: 'Documentation' },
] as const;

export function PublicLayout({ children }: { readonly children: ReactNode }): JSX.Element {
  return (
    <div style={{ minHeight: '100vh', display: 'grid', gridTemplateRows: 'auto 1fr auto', background: color.surface0 }}>
      <header style={{
        height: 56, display: 'flex', alignItems: 'center', gap: space[4], padding: `0 ${space[5]}px`,
        borderBottom: `1px solid ${color.border}`, background: color.surface1,
      }}>
        <Link to="/" style={{ display: 'inline-flex', alignItems: 'center', gap: space[2], textDecoration: 'none', color: color.fg }}>
          <span aria-hidden="true" style={{ width: 20, height: 20, borderRadius: 5, background: color.primary }} />
          <strong style={{ fontSize: fontSize.base, letterSpacing: '-0.01em' }}>LabConnect</strong>
        </Link>
        <nav aria-label="Primary" className="lc-public-nav" style={{ display: 'flex', gap: space[3], marginLeft: space[3] }}>
          {NAV.map((n) => (
            <NavLink key={n.to} to={n.to}
              style={({ isActive }) => ({
                fontSize: fontSize.body, textDecoration: 'none',
                color: isActive ? color.fg : color.fgMuted, fontWeight: 500,
              })}>
              {n.label}
            </NavLink>
          ))}
        </nav>
        <div style={{ marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: space[3] }}>
          <Link to="/sign-in" className="lc-btn lc-btn--ghost">Sign in</Link>
          <Link to="/sign-in" className="lc-btn lc-btn--primary">Get started</Link>
        </div>
      </header>

      <main>{children}</main>

      <footer style={{
        borderTop: `1px solid ${color.border}`, background: color.surface1,
        padding: `${space[5]}px`, display: 'flex', gap: space[4], flexWrap: 'wrap',
        alignItems: 'center', fontSize: fontSize.meta, color: color.fgMuted,
      }}>
        <span>© LabConnect. Laboratory analyzer connectivity.</span>
        <nav aria-label="Legal" style={{ display: 'flex', gap: space[4], marginLeft: 'auto', flexWrap: 'wrap' }}>
          <Link to="/legal/terms" style={{ color: color.fgMuted }}>Terms</Link>
          <Link to="/legal/privacy" style={{ color: color.fgMuted }}>Privacy</Link>
          <Link to="/security" style={{ color: color.fgMuted }}>Security</Link>
          <Link to="/status" style={{ color: color.fgMuted }}>Status</Link>
        </nav>
      </footer>
    </div>
  );
}

/** Constrained content column shared by the public pages. */
export function Prose({ title, lead, children }: {
  readonly title: string; readonly lead?: string; readonly children?: ReactNode;
}): JSX.Element {
  return (
    <section style={{ maxWidth: 760, margin: '0 auto', padding: `${space[6] ?? 48}px ${space[5]}px` }}>
      <h1 style={{ fontSize: fontSize.hero, fontWeight: 700, letterSpacing: '-0.02em', marginBottom: space[3] }}>{title}</h1>
      {lead !== undefined && (
        <p style={{ fontSize: fontSize.section, color: color.fgMuted, marginBottom: space[5], lineHeight: 1.5 }}>{lead}</p>
      )}
      {children}
    </section>
  );
}
