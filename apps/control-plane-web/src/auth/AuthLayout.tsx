// Centered layout for unauthenticated flows (sign-in, reset, verify, invite).
// Calm and narrow: one task per screen, product mark, no marketing chrome.

import type { ReactNode } from 'react';
import { color, fontSize, space } from '@lab-connect/ui';

export function AuthLayout({
  title,
  intro,
  children,
  footer,
}: {
  readonly title: string;
  readonly intro?: string;
  readonly children: ReactNode;
  readonly footer?: ReactNode;
}): JSX.Element {
  return (
    <main
      style={{
        minHeight: '100vh',
        display: 'grid',
        placeItems: 'center',
        padding: space[5],
        background: color.surface0,
      }}
    >
      <div style={{ width: '100%', maxWidth: 400, display: 'grid', gap: space[4] }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: space[2] }}>
          <span
            aria-hidden="true"
            style={{
              width: 24, height: 24, borderRadius: 6,
              background: color.primary, display: 'inline-block',
            }}
          />
          <span style={{ fontSize: fontSize.section, fontWeight: 700, letterSpacing: '-0.01em' }}>
            LabConnect
          </span>
        </div>

        <div className="lc-card" style={{ padding: space[5], display: 'grid', gap: space[4] }}>
          <div style={{ display: 'grid', gap: space[1] }}>
            <h1 style={{ fontSize: fontSize.title, fontWeight: 600 }}>{title}</h1>
            {intro !== undefined && (
              <p style={{ margin: 0, fontSize: fontSize.body, color: color.fgMuted }}>{intro}</p>
            )}
          </div>
          {children}
        </div>

        {footer !== undefined && (
          <div style={{ fontSize: fontSize.meta, color: color.fgMuted, textAlign: 'center' }}>
            {footer}
          </div>
        )}
      </div>
    </main>
  );
}

/** Inline error region shared by the auth forms (announced to assistive tech). */
export function FormError({ message }: { readonly message: string | null }): JSX.Element | null {
  if (message === null) {
    return null;
  }
  return (
    <p
      role="alert"
      style={{
        margin: 0,
        padding: `${space[2]}px ${space[3]}px`,
        borderRadius: 4,
        fontSize: fontSize.body,
        color: color.danger,
        border: `1px solid ${color.danger}`,
        background: 'color-mix(in oklch, var(--lc-danger) 8%, transparent)',
      }}
    >
      {message}
    </p>
  );
}
