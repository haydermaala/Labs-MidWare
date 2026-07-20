// Security: email verification, guided MFA enrollment, and session management.
//
// MFA enrollment is a strict sequence: setup yields a pending secret (shown as
// text + otpauth URI for manual entry — every authenticator supports it), the
// user proves a live code to arm it, and the recovery codes are displayed
// exactly once with an explicit acknowledgement before they disappear.

import { useCallback, useEffect, useState } from 'react';
import {
  disableMfa, enableMfa, listSessions, revokeAllSessions, sendVerification, setupMfa,
  type MfaSetup, type SessionInfo,
} from '@lab-connect/api-client';
import { Button, Field, StatusBadge, color, fontSize, space } from '@lab-connect/ui';
import { API_BASE } from '../config';
import { useAuth, options } from '../auth/AuthProvider';
import { PageHeader } from './Pages';
import { CopyField } from '../shell/Drawer';

function fmt(instant: string): string {
  const d = new Date(instant);
  return Number.isNaN(d.getTime()) ? instant : d.toISOString().replace('T', ' ').slice(0, 16) + 'Z';
}

export function SecurityPage(): JSX.Element {
  const { user, token, refresh } = useAuth();

  return (
    <>
      <PageHeader title="Security" description="Account protection for your LabConnect sign-in." />
      <div style={{ display: 'grid', gap: space[4], maxWidth: 720 }}>
        <EmailSection verified={user?.emailVerified === true} email={user?.email ?? ''} token={token} onChanged={refresh} />
        <MfaSection enabled={user?.mfaEnabled === true} token={token} onChanged={refresh} />
        <SessionsSection token={token} />
      </div>
    </>
  );
}

function Section({ title, status, children }: {
  readonly title: string;
  readonly status?: 'active' | 'inactive' | 'pending';
  readonly children: React.ReactNode;
}): JSX.Element {
  return (
    <section className="lc-card" style={{ padding: space[4], display: 'grid', gap: space[3] }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: space[3] }}>
        <h2 style={{ fontSize: fontSize.section, fontWeight: 600 }}>{title}</h2>
        {status !== undefined && <StatusBadge status={status} />}
      </div>
      {children}
    </section>
  );
}

function EmailSection({ verified, email, token, onChanged }: {
  readonly verified: boolean; readonly email: string;
  readonly token: string | null; readonly onChanged: () => Promise<void>;
}): JSX.Element {
  const [sent, setSent] = useState(false);
  const [busy, setBusy] = useState(false);

  async function send(): Promise<void> {
    if (token === null) return;
    setBusy(true);
    try {
      await sendVerification({ baseUrl: API_BASE, sessionToken: token });
    } finally {
      setSent(true);
      setBusy(false);
      await onChanged();
    }
  }

  return (
    <Section title="Email address" status={verified ? 'active' : 'pending'}>
      <p style={{ margin: 0, color: color.fgMuted, fontSize: fontSize.body }}>
        {email} — {verified ? 'verified.' : 'not verified. Verification is required before you can receive operational alerts.'}
      </p>
      {!verified && (
        <div>
          <Button onClick={() => void send()} loading={busy} disabled={sent}>
            {sent ? 'Verification sent — check your inbox' : 'Send verification email'}
          </Button>
        </div>
      )}
    </Section>
  );
}

type MfaFlow =
  | { readonly step: 'idle' }
  | { readonly step: 'confirm-secret'; readonly setup: MfaSetup }
  | { readonly step: 'recovery'; readonly codes: readonly string[] }
  | { readonly step: 'disabling' };

function MfaSection({ enabled, token, onChanged }: {
  readonly enabled: boolean; readonly token: string | null; readonly onChanged: () => Promise<void>;
}): JSX.Element {
  const [flow, setFlow] = useState<MfaFlow>({ step: 'idle' });
  const [code, setCode] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const opts = { baseUrl: API_BASE, sessionToken: token ?? '' };

  async function begin(): Promise<void> {
    setBusy(true);
    setError(null);
    try {
      setFlow({ step: 'confirm-secret', setup: await setupMfa(opts) });
      setCode('');
    } catch {
      setError('Could not start enrollment. If MFA is already enabled, disable it first.');
    } finally {
      setBusy(false);
    }
  }

  async function arm(event: React.FormEvent): Promise<void> {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const codes = await enableMfa(opts, code);
      setFlow({ step: 'recovery', codes });
      setCode('');
      await onChanged();
    } catch {
      setError('That code was not accepted. Check the app and enter the current 6-digit code.');
    } finally {
      setBusy(false);
    }
  }

  async function disable(event: React.FormEvent): Promise<void> {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await disableMfa(opts, code);
      setFlow({ step: 'idle' });
      setCode('');
      await onChanged();
    } catch {
      setError('That code was not accepted. A current authenticator code is required to disable MFA.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <Section title="Two-factor authentication" status={enabled ? 'active' : 'inactive'}>
      {error !== null && (
        <p role="alert" style={{ margin: 0, color: color.danger, fontSize: fontSize.body }}>{error}</p>
      )}

      {flow.step === 'idle' && !enabled && (
        <>
          <p style={{ margin: 0, color: color.fgMuted, fontSize: fontSize.body }}>
            Require a 6-digit authenticator code at sign-in. Strongly recommended for administrators.
          </p>
          <div><Button onClick={() => void begin()} loading={busy}>Set up two-factor authentication</Button></div>
        </>
      )}

      {flow.step === 'idle' && enabled && (
        <>
          <p style={{ margin: 0, color: color.fgMuted, fontSize: fontSize.body }}>
            Your account requires an authenticator code at sign-in. Disabling requires a current code
            and permanently invalidates your recovery codes.
          </p>
          <div><Button variant="danger" onClick={() => setFlow({ step: 'disabling' })}>Disable…</Button></div>
        </>
      )}

      {flow.step === 'confirm-secret' && (
        <form onSubmit={(e) => void arm(e)} style={{ display: 'grid', gap: space[3] }}>
          <p style={{ margin: 0, color: color.fgMuted, fontSize: fontSize.body }}>
            Add this secret to your authenticator app (1Password, Google Authenticator, Authy, …) by
            manual entry, then confirm with the app's current code. The secret is shown only now.
          </p>
          <CopyField label="Secret key (enter manually in the app)" value={flow.setup.secret} />
          <CopyField label="Or paste the setup link (otpauth URI)" value={flow.setup.provisioningUri} />
          <Field
            label="Current 6-digit code from the app"
            value={code}
            onChange={(e) => setCode(e.target.value)}
            inputMode="numeric"
            autoComplete="one-time-code"
            required
          />
          <div style={{ display: 'flex', gap: space[2] }}>
            <Button type="submit" loading={busy}>Verify and enable</Button>
            <Button variant="secondary" onClick={() => { setFlow({ step: 'idle' }); setError(null); }}>Cancel</Button>
          </div>
        </form>
      )}

      {flow.step === 'recovery' && (
        <div style={{ display: 'grid', gap: space[3] }}>
          <p role="status" style={{
            margin: 0, padding: `${space[2]}px ${space[3]}px`, borderRadius: 4,
            fontSize: fontSize.body, color: color.warn,
            border: `1px solid ${color.warn}`, background: 'color-mix(in oklch, var(--lc-warn) 8%, transparent)',
          }}>
            Save these recovery codes now — each works once, and they will never be shown again. They
            are the only way in if you lose your authenticator.
          </p>
          <ul className="lc-mono lc-tabular" style={{
            margin: 0, padding: space[3], listStyle: 'none',
            display: 'grid', gridTemplateColumns: '1fr 1fr', gap: space[2],
            background: color.surface2, borderRadius: 4, fontSize: 13,
          }}>
            {flow.codes.map((c) => <li key={c}>{c}</li>)}
          </ul>
          <div style={{ display: 'flex', gap: space[2] }}>
            <Button variant="secondary" onClick={() => { void navigator.clipboard?.writeText(flow.codes.join('\n')); }}>
              Copy all
            </Button>
            <Button onClick={() => setFlow({ step: 'idle' })}>I have saved my recovery codes</Button>
          </div>
        </div>
      )}

      {flow.step === 'disabling' && (
        <form onSubmit={(e) => void disable(e)} style={{ display: 'grid', gap: space[3] }}>
          <Field
            label="Current 6-digit code from the app"
            value={code}
            onChange={(e) => setCode(e.target.value)}
            inputMode="numeric"
            autoComplete="one-time-code"
            help="Disabling permanently invalidates your recovery codes."
            required
          />
          <div style={{ display: 'flex', gap: space[2] }}>
            <Button type="submit" variant="danger" loading={busy}>Disable two-factor authentication</Button>
            <Button variant="secondary" onClick={() => { setFlow({ step: 'idle' }); setError(null); }}>Cancel</Button>
          </div>
        </form>
      )}
    </Section>
  );
}

function SessionsSection({ token }: { readonly token: string | null }): JSX.Element {
  const { signOut } = useAuth();
  const [sessions, setSessions] = useState<readonly SessionInfo[] | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async (): Promise<void> => {
    if (token === null) return;
    try {
      setSessions(await listSessions({ baseUrl: API_BASE, sessionToken: token }));
    } catch {
      setSessions([]);
    }
  }, [token]);

  useEffect(() => { void load(); }, [load]);

  async function revokeAll(): Promise<void> {
    if (token === null) return;
    setBusy(true);
    try {
      await revokeAllSessions({ baseUrl: API_BASE, sessionToken: token });
    } finally {
      // Every session (including this one) is dead; sign out locally too.
      await signOut();
    }
  }

  return (
    <Section title="Sessions">
      <p style={{ margin: 0, color: color.fgMuted, fontSize: fontSize.body }}>
        Devices currently signed in to your account. If anything looks unfamiliar, sign out
        everywhere — every session, including this one, is revoked immediately.
      </p>
      {sessions === null ? (
        <div aria-hidden="true" style={{ height: 36, borderRadius: 4, background: color.surface2 }} />
      ) : (
        <ul style={{ margin: 0, padding: 0, listStyle: 'none', display: 'grid', gap: space[2] }}>
          {sessions.map((s) => (
            <li key={s.id} style={{
              display: 'flex', alignItems: 'center', gap: space[3], flexWrap: 'wrap',
              padding: `${space[2]}px ${space[3]}px`, border: `1px solid ${color.border}`, borderRadius: 4,
              fontSize: fontSize.table,
            }}>
              <span style={{ fontWeight: 600 }}>{s.current ? 'This device' : 'Signed-in device'}</span>
              <span className="lc-tabular" style={{ color: color.fgMuted }}>last seen {fmt(s.lastSeenAt)}</span>
              <span className="lc-tabular" style={{ color: color.fgMuted, marginLeft: 'auto' }}>expires {fmt(s.expiresAt)}</span>
            </li>
          ))}
        </ul>
      )}
      <div>
        <Button variant="danger" loading={busy} onClick={() => void revokeAll()}>
          Sign out everywhere
        </Button>
      </div>
    </Section>
  );
}
