// Token-driven account flows: forgot password, reset password, verify email,
// and invitation acceptance. Each is a single focused task with explicit
// success/error states; links are single-use and time-bounded server-side.

import { useCallback, useEffect, useState } from 'react';
import { Link, Navigate, useSearchParams } from 'react-router-dom';
import {
  acceptInvitation, forgotPassword, resetPassword, verifyEmail,
} from '@lab-connect/api-client';
import { Button, Field, color, fontSize, space } from '@lab-connect/ui';
import { API_BASE } from '../config';
import { useAuth, options } from './AuthProvider';
import { AuthLayout, FormError } from './AuthLayout';

const anon = { baseUrl: API_BASE };

/** Request a reset link. Always reports the same outcome (no account oracle). */
export function ForgotPasswordPage(): JSX.Element {
  const [email, setEmail] = useState('');
  const [sent, setSent] = useState(false);
  const [busy, setBusy] = useState(false);

  async function submit(event: React.FormEvent): Promise<void> {
    event.preventDefault();
    setBusy(true);
    try {
      await forgotPassword(anon, email);
    } catch {
      /* deliberately indistinguishable from success */
    } finally {
      setBusy(false);
      setSent(true);
    }
  }

  if (sent) {
    return (
      <AuthLayout
        title="Check your email"
        intro="If an account exists for that address, we have sent a link to reset its password. The link expires in one hour."
        footer={<Link to="/sign-in" style={{ color: color.primary }}>Back to sign in</Link>}
      >
        <span />
      </AuthLayout>
    );
  }

  return (
    <AuthLayout
      title="Reset your password"
      intro="Enter your email address and we will send you a reset link."
      footer={<Link to="/sign-in" style={{ color: color.primary }}>Back to sign in</Link>}
    >
      <form onSubmit={(e) => void submit(e)} style={{ display: 'grid', gap: space[4] }}>
        <Field
          label="Email address"
          type="email"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          autoComplete="username"
          autoFocus
          required
        />
        <Button type="submit" loading={busy}>Send reset link</Button>
      </form>
    </AuthLayout>
  );
}

/** Complete a reset. Succeeding revokes every existing session server-side. */
export function ResetPasswordPage(): JSX.Element {
  const [params] = useSearchParams();
  const token = params.get('token') ?? '';
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);
  const [busy, setBusy] = useState(false);

  async function submit(event: React.FormEvent): Promise<void> {
    event.preventDefault();
    if (password !== confirm) {
      setError('Both passwords must match.');
      return;
    }
    if (password.length < 12) {
      setError('Use at least 12 characters.');
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await resetPassword(anon, token, password);
      setDone(true);
    } catch {
      setError('That link is invalid or has expired. Request a new one.');
    } finally {
      setBusy(false);
    }
  }

  if (done) {
    return (
      <AuthLayout
        title="Password updated"
        intro="All other sessions have been signed out. Sign in with your new password."
        footer={<Link to="/sign-in" style={{ color: color.primary }}>Go to sign in</Link>}
      >
        <span />
      </AuthLayout>
    );
  }

  return (
    <AuthLayout title="Choose a new password" intro="Use at least 12 characters. A memorable passphrase is stronger than a short complex password.">
      <form onSubmit={(e) => void submit(e)} style={{ display: 'grid', gap: space[4] }}>
        <FormError message={error} />
        <Field
          label="New password"
          type="password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          autoComplete="new-password"
          help="12 characters minimum"
          autoFocus
          required
        />
        <Field
          label="Confirm new password"
          type="password"
          value={confirm}
          onChange={(e) => setConfirm(e.target.value)}
          autoComplete="new-password"
          required
        />
        <Button type="submit" loading={busy}>Update password</Button>
      </form>
    </AuthLayout>
  );
}

type Outcome = 'working' | 'ok' | 'failed';

/** Consume an email-verification link on load. */
export function VerifyEmailPage(): JSX.Element {
  const [params] = useSearchParams();
  const token = params.get('token') ?? '';
  const [outcome, setOutcome] = useState<Outcome>('working');

  const run = useCallback(async () => {
    try {
      await verifyEmail(anon, token);
      setOutcome('ok');
    } catch {
      setOutcome('failed');
    }
  }, [token]);

  useEffect(() => { void run(); }, [run]);

  return (
    <AuthLayout
      title={outcome === 'ok' ? 'Email verified' : outcome === 'failed' ? 'Link not valid' : 'Verifying…'}
      intro={
        outcome === 'ok' ? 'Thank you — your email address is confirmed.'
          : outcome === 'failed' ? 'This link is invalid, already used, or expired. Request a new one from your account settings.'
            : 'Confirming your email address.'
      }
      footer={<Link to="/sign-in" style={{ color: color.primary }}>Continue to sign in</Link>}
    >
      <span />
    </AuthLayout>
  );
}

/** Accept a tenant invitation. Requires being signed in as the invited address. */
export function AcceptInvitePage(): JSX.Element {
  const [params] = useSearchParams();
  const token = params.get('token') ?? '';
  const auth = useAuth();
  const [outcome, setOutcome] = useState<Outcome | 'idle'>('idle');
  const [tenantName, setTenantName] = useState('');

  async function accept(): Promise<void> {
    if (auth.token === null) {
      return;
    }
    setOutcome('working');
    try {
      const membership = await acceptInvitation(options(auth.token), token);
      setTenantName(membership.tenantName);
      setOutcome('ok');
      await auth.refresh();
    } catch {
      setOutcome('failed');
    }
  }

  if (auth.status === 'loading') {
    return <AuthLayout title="Loading…"><span /></AuthLayout>;
  }

  // Invitations are bound to the invited email, so the operator signs in first.
  if (auth.status === 'signed-out') {
    return (
      <AuthLayout
        title="Sign in to accept"
        intro="This invitation is tied to a specific email address. Sign in with that address, then open the link again."
        footer={<Link to="/sign-in" style={{ color: color.primary }}>Go to sign in</Link>}
      >
        <span />
      </AuthLayout>
    );
  }

  if (outcome === 'ok') {
    return <Navigate to="/" replace state={{ joined: tenantName }} />;
  }

  return (
    <AuthLayout
      title="Accept invitation"
      intro={`You are signed in as ${auth.user?.email ?? ''}. Accepting adds this laboratory to your account.`}
    >
      <div style={{ display: 'grid', gap: space[4] }}>
        <FormError message={outcome === 'failed'
          ? 'This invitation is invalid, expired, already used, or was issued to a different email address.'
          : null} />
        <Button onClick={() => void accept()} loading={outcome === 'working'}>Accept invitation</Button>
        <span style={{ fontSize: fontSize.meta, color: color.fgMuted }}>
          Not you? Sign out and sign in with the invited address.
        </span>
      </div>
    </AuthLayout>
  );
}
