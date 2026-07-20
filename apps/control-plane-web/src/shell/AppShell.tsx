// Authenticated shell (DESIGN.md §4): 56px header, 240px sidebar collapsing to
// a 64px icon rail under 1024px and to an off-canvas drawer under 768px.
//
// Layout lives in `shellCss` classes rather than inline styles so the media
// queries can actually take effect — inline styles win over stylesheet rules,
// which silently breaks responsive behaviour.

import { useEffect, useState } from 'react';
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom';
import { color, fontSize, space } from '@lab-connect/ui';
import { useAuth } from '../auth/AuthProvider';
import { TenantSwitcher } from './TenantSwitcher';

// Lucide 24×24 outlines — one icon system across the product.
const ICONS = {
  dashboard: 'M3 13h8V3H3zM13 21h8V11h-8zM13 3v6h8V3zM3 21h8v-6H3z',
  fleet: 'M5 12V7a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2v5M4 12h16a1 1 0 0 1 1 1v4a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1v-4a1 1 0 0 1 1-1zM7 16h.01M11 16h.01',
  audit: 'M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8zM14 2v6h6M9 15h6M9 11h3',
  people: 'M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8zM22 21v-2a4 4 0 0 0-3-3.87M16 3.13a4 4 0 0 1 0 7.75',
  security: 'M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z',
  settings: 'M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2zM12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z',
  menu: 'M4 6h16M4 12h16M4 18h16',
} as const;

function Icon({ path }: { readonly path: string }): JSX.Element {
  return (
    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor"
      strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d={path} />
    </svg>
  );
}

const NAV = [
  { to: '/', label: 'Dashboard', icon: ICONS.dashboard, end: true },
  { to: '/fleet', label: 'Fleet', icon: ICONS.fleet, end: false },
  { to: '/people', label: 'People', icon: ICONS.people, end: false },
  { to: '/audit', label: 'Audit', icon: ICONS.audit, end: false },
  { to: '/security', label: 'Security', icon: ICONS.security, end: false },
  { to: '/settings', label: 'Settings', icon: ICONS.settings, end: false },
] as const;

export function AppShell(): JSX.Element {
  const auth = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const [navOpen, setNavOpen] = useState(false);

  // Navigating on a phone should close the drawer.
  useEffect(() => { setNavOpen(false); }, [location.pathname]);

  async function signOut(): Promise<void> {
    await auth.signOut();
    navigate('/sign-in', { replace: true });
  }

  return (
    <div className="lc-shell">
      <a href="#main" className="lc-skip">Skip to content</a>

      <header className="lc-header">
        <button
          type="button"
          className="lc-btn lc-btn--ghost lc-nav-toggle"
          aria-label={navOpen ? 'Hide navigation' : 'Show navigation'}
          aria-expanded={navOpen}
          aria-controls="lc-primary-nav"
          onClick={() => setNavOpen((v) => !v)}
        >
          <Icon path={ICONS.menu} />
        </button>

        <span className="lc-brand">
          <span aria-hidden="true" className="lc-brand__mark" />
          <strong>LabConnect</strong>
        </span>

        <TenantSwitcher />

        <div className="lc-header__end">
          <span className="lc-user-email">{auth.user?.email}</span>
          <button type="button" className="lc-btn lc-btn--secondary" onClick={() => void signOut()}>
            Sign out
          </button>
        </div>
      </header>

      <div className="lc-body">
        {navOpen && (
          <button
            type="button"
            className="lc-scrim"
            aria-label="Close navigation"
            onClick={() => setNavOpen(false)}
          />
        )}

        <nav
          id="lc-primary-nav"
          aria-label="Primary"
          data-open={navOpen ? 'true' : 'false'}
          className="lc-sidebar"
        >
          {NAV.map((item) => (
            <NavLink
              key={item.to}
              to={item.to}
              end={item.end}
              className="lc-nav-item"
              style={({ isActive }) => ({
                color: isActive ? color.primary : color.fgMuted,
                background: isActive ? color.surface2 : 'transparent',
              })}
            >
              <Icon path={item.icon} />
              <span className="lc-nav-label">{item.label}</span>
            </NavLink>
          ))}
        </nav>

        <main id="main" className="lc-main">
          <Outlet />
        </main>
      </div>
    </div>
  );
}

/** Shell layout + responsive rules (DESIGN.md §4 breakpoints 375/768/1024/1440). */
export const shellCss = `
.lc-shell { min-height: 100vh; background: var(--lc-surface-0); }

.lc-skip {
  position: absolute; left: -9999px; top: 8px; z-index: 100;
  background: var(--lc-surface-1); color: var(--lc-fg);
  padding: 8px 12px; border: 1px solid var(--lc-border); border-radius: 4px;
  text-decoration: none;
}
.lc-skip:focus { left: 8px; }

.lc-header {
  height: 56px; display: flex; align-items: center; gap: 12px;
  padding: 0 16px; background: var(--lc-surface-1);
  border-bottom: 1px solid var(--lc-border);
  position: sticky; top: 0; z-index: 20;
}
.lc-brand { display: inline-flex; align-items: center; gap: 8px; flex-shrink: 0; }
.lc-brand strong { font-size: 16px; letter-spacing: -0.01em; }
.lc-brand__mark { width: 20px; height: 20px; border-radius: 5px; background: var(--lc-primary); }
.lc-header__end { margin-left: auto; display: flex; align-items: center; gap: 12px; flex-shrink: 0; }
.lc-user-email { font-size: 12px; color: var(--lc-fg-muted); }
.lc-nav-toggle { display: none; padding: 6px; }

.lc-body { display: flex; align-items: stretch; }

.lc-sidebar {
  width: 240px; flex-shrink: 0; padding: 8px;
  display: grid; gap: 2px; align-content: start;
  background: var(--lc-surface-1); border-right: 1px solid var(--lc-border);
  position: sticky; top: 56px; height: calc(100vh - 56px);
}
.lc-nav-item {
  display: flex; align-items: center; gap: 8px;
  height: 34px; padding: 0 12px; border-radius: 4px;
  font-size: 14px; font-weight: 500; text-decoration: none;
}
.lc-nav-item:hover { background: var(--lc-surface-2) !important; color: var(--lc-fg) !important; }

.lc-main { flex: 1; min-width: 0; padding: 24px; }

.lc-scrim {
  display: none; position: fixed; inset: 56px 0 0 0; z-index: 25;
  background: rgba(0,0,0,.5); border: 0; padding: 0;
}

/* Tablet: icon rail. */
@media (max-width: 1023px) {
  .lc-sidebar { width: 64px; }
  .lc-nav-label { display: none; }
  .lc-nav-item { justify-content: center; padding: 0; }
}

/* Phone: off-canvas drawer; sidebar leaves the flow entirely. */
@media (max-width: 767px) {
  .lc-nav-toggle { display: inline-flex; }
  .lc-user-email { display: none; }
  .lc-main { padding: 16px; }
  .lc-scrim { display: block; }
  .lc-sidebar {
    position: fixed; top: 56px; left: 0; bottom: 0; height: auto;
    width: 240px; z-index: 30;
    transform: translateX(-100%); transition: transform 150ms ease;
  }
  .lc-sidebar[data-open="true"] { transform: translateX(0); }
  .lc-sidebar .lc-nav-label { display: inline; }
  .lc-sidebar .lc-nav-item { justify-content: flex-start; padding: 0 12px; }
}
@media (prefers-reduced-motion: reduce) { .lc-sidebar { transition: none; } }
`;

/** Shared page-level helpers used by the shell's children. */
export const pageGap = space[5];
export const pageMuted = { color: color.fgMuted, fontSize: fontSize.body };
