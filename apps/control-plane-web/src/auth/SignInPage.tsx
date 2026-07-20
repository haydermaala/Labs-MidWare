// Sign-in: password step, then the MFA step when the account requires it.
// The server never reveals whether an email exists, so failures are phrased
// generically here too.

import { useState } from 'react';
import { Link, Navigate } from 'react-router-dom';
import { Button, Field, color, fontSize, space } from '@lab-connect/ui';
import { useAuth } from './AuthProvider';
import { AuthLayout, FormError } from './AuthLayout';

type Step =
  | { readonly name: 'password' }
  | { readonly name: 'mfa'; readonly mfaToken: string; readonly recovery: boolean };

export function SignInPage(): JSX.Element {
  const auth = useAuth();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [code, setCode] = useState('');
  const [step, setStep] = useState<Step>({ name: 'password' });
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  if (auth.status === 'signed-in') {
    return <Navigate to="/" replace />;
  }

  async function submitPassword(event: React.FormEvent): Promise<void> {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const outcome = await auth.signIn(email, password);
      if (outcome.kind === 'mfa') {
        setStep({ name: 'mfa', mfaToken: outcome.mfaToken, recovery: false });
      }
    } catch {
      setError('That email and password combination was not recognized.');
    } finally {
      setBusy(false);
    }
  }

  async function submitMfa(event: React.FormEvent): Promise<void> {
    event.preventDefault();
    if (step.name !== 'mfa') {
      return;
    }
    setBusy(true);
    setError(null);
    try {
      if (step.recovery) {
        await auth.completeRecovery(step.mfaToken, code);
      } else {
        await auth.completeMfa(step.mfaToken, code);
      }
    } catch {
      // The challenge is single-use: send the operator back to the password step.
      setStep({ name: 'password' });
      setCode('');
      setError('That code was not accepted. Please sign in again.');
    } finally {
      setBusy(false);
    }
  }

  if (step.name === 'mfa') {
    return (
      <AuthLayout
        title="Two-factor authentication"
        intro={step.recovery
          ? 'Enter one of your saved recovery codes.'
          : 'Enter the 6-digit code from your authenticator app.'}
      >
        <form onSubmit={(e) => void submitMfa(e)} style={{ display: 'grid', gap: space[4] }}>
          <FormError message={error} />
          <Field
            label={step.recovery ? 'Recovery code' : 'Authentication code'}
            value={code}
            onChange={(e) => setCode(e.target.value)}
            autoComplete="one-time-code"
            inputMode={step.recovery ? 'text' : 'numeric'}
            autoFocus
            required
          />
          <Button type="submit" loading={busy}>Verify</Button>
          <button
            type="button"
            className="lc-btn lc-btn--ghost"
            onClick={() => { setStep({ ...step, recovery: !step.recovery }); setCode(''); }}
          >
            {step.recovery ? 'Use authenticator app instead' : 'Use a recovery code instead'}
          </button>
        </form>
      </AuthLayout>
    );
  }

  return (
    <AuthLayout
      title="Sign in"
      intro="Access your laboratory's connectivity control plane."
      footer={<>Trouble signing in? Contact your laboratory administrator.</>}
    >
      <form onSubmit={(e) => void submitPassword(e)} style={{ display: 'grid', gap: space[4] }}>
        <FormError message={error} />
        <Field
          label="Email address"
          type="email"
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          autoComplete="username"
          autoFocus
          required
        />
        <div style={{ display: 'grid', gap: space[1] }}>
          <Field
            label="Password"
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete="current-password"
            required
          />
          <Link
            to="/forgot-password"
            style={{ fontSize: fontSize.meta, color: color.primary, justifySelf: 'end' }}
          >
            Forgot your password?
          </Link>
        </div>
        <Button type="submit" loading={busy}>Sign in</Button>
      </form>
    </AuthLayout>
  );
}
