// Step-up re-authentication (ADR 0019). High-risk actions (decommission a
// gateway, change/remove a member, tenant lifecycle, billing changes) require a
// recent sign-in. When one returns 403 with `requiresStepUp`, `guard(action)`
// prompts for the password (and an MFA code when enabled), refreshes the session's
// fresh-auth window via /api/auth/step-up, and retries the action once. Any other
// error propagates unchanged.

import { createContext, useCallback, useContext, useMemo, useRef, useState } from 'react';
import type { CSSProperties, FormEvent, ReactNode } from 'react';
import { ApiError } from '@lab-connect/api-client';
import { Button, Field, color, fontSize, radius, shadow, space } from '@lab-connect/ui';
import { useAuth } from './AuthProvider';

/** Thrown by a guarded action when the user dismisses the step-up prompt. */
export class StepUpCancelledError extends Error {
  constructor() {
    super('step-up cancelled');
    this.name = 'StepUpCancelledError';
  }
}

interface StepUpContextValue {
  /** Run an action; if it fails only because it needs re-authentication, prompt
   * for step-up and retry it once. Other failures propagate unchanged. */
  guard<T>(action: () => Promise<T>): Promise<T>;
}

const StepUpContext = createContext<StepUpContextValue | null>(null);

export function useStepUp(): StepUpContextValue {
  const ctx = useContext(StepUpContext);
  if (ctx === null) {
    throw new Error('useStepUp must be used inside <StepUpProvider>');
  }
  return ctx;
}

const overlayStyle: CSSProperties = {
  position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.5)',
  display: 'flex', alignItems: 'center', justifyContent: 'center', padding: space[4], zIndex: 1000,
};

const cardStyle: CSSProperties = {
  background: color.surface0, border: `1px solid ${color.border}`, borderRadius: radius.card,
  boxShadow: shadow.lg, padding: space[5], width: '100%', maxWidth: 380,
  display: 'flex', flexDirection: 'column', gap: space[3],
};

export function StepUpProvider({ children }: { readonly children: ReactNode }): JSX.Element {
  const { user, stepUp } = useAuth();
  const [open, setOpen] = useState(false);
  const [password, setPassword] = useState('');
  const [code, setCode] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const pending = useRef<{ resolve: () => void; reject: (reason: unknown) => void } | null>(null);

  const close = useCallback((settle: 'resolve' | 'reject', reason?: unknown): void => {
    setOpen(false);
    setPassword('');
    setCode('');
    setError(null);
    setBusy(false);
    const p = pending.current;
    pending.current = null;
    if (p !== null) {
      if (settle === 'resolve') {
        p.resolve();
      } else {
        p.reject(reason);
      }
    }
  }, []);

  const guard = useCallback(async function guard<T>(action: () => Promise<T>): Promise<T> {
    try {
      return await action();
    } catch (e) {
      if (e instanceof ApiError && e.requiresStepUp) {
        await new Promise<void>((resolve, reject) => {
          pending.current = { resolve, reject };
          setOpen(true);
        });
        return await action(); // retry once after a successful step-up
      }
      throw e;
    }
  }, []);

  async function submit(e: FormEvent<HTMLFormElement>): Promise<void> {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await stepUp(password, user?.mfaEnabled === true ? code : undefined);
      close('resolve');
    } catch {
      setError(`Re-authentication failed. Check your password${user?.mfaEnabled === true ? ' and code.' : '.'}`);
      setBusy(false);
    }
  }

  const value = useMemo<StepUpContextValue>(() => ({ guard }), [guard]);

  return (
    <StepUpContext.Provider value={value}>
      {children}
      {open && (
        <div style={overlayStyle} role="presentation" onMouseDown={() => close('reject', new StepUpCancelledError())}>
          <form
            style={cardStyle}
            role="dialog"
            aria-modal="true"
            aria-label="Confirm your identity"
            onSubmit={submit}
            onMouseDown={(ev) => ev.stopPropagation()}
          >
            <h2 style={{ margin: 0, fontSize: fontSize.section }}>Confirm it&rsquo;s you</h2>
            <p style={{ margin: 0, color: color.fgMuted, fontSize: fontSize.body }}>
              This action needs a recent sign-in. Re-enter your password to continue.
            </p>
            <Field
              label="Password"
              type="password"
              autoFocus
              autoComplete="current-password"
              value={password}
              onChange={(ev) => setPassword(ev.target.value)}
            />
            {user?.mfaEnabled === true && (
              <Field
                label="Authenticator code"
                inputMode="numeric"
                autoComplete="one-time-code"
                value={code}
                onChange={(ev) => setCode(ev.target.value)}
              />
            )}
            {error !== null && (
              <p role="alert" style={{ margin: 0, color: color.danger, fontSize: fontSize.body }}>{error}</p>
            )}
            <div style={{ display: 'flex', gap: space[2], justifyContent: 'flex-end', marginTop: space[2] }}>
              <Button type="button" variant="ghost" onClick={() => close('reject', new StepUpCancelledError())}>
                Cancel
              </Button>
              <Button type="submit" loading={busy}>Confirm</Button>
            </div>
          </form>
        </div>
      )}
    </StepUpContext.Provider>
  );
}
