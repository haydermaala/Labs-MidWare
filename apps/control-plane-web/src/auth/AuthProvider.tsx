// Session state for the console.
//
// The token is held in React state and mirrored to sessionStorage so a page
// refresh during a working session does not sign the operator out. This is a
// deliberate, documented development-stage tradeoff: at launch the console is
// served same-origin and authenticates with the HttpOnly `lc_session` cookie the
// API already sets, at which point the storage mirror is removed (Phase H).

import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import {
  login as apiLogin,
  logout as apiLogout,
  me as apiMe,
  myMemberships,
  verifyMfa as apiVerifyMfa,
  recoverMfa as apiRecoverMfa,
  type AuthOptions,
  type AuthUser,
  type LoginOutcome,
  type Membership,
  type SessionResult,
} from '@lab-connect/api-client';
import { API_BASE } from '../config';

const TOKEN_KEY = 'lc.session';
const TENANT_KEY = 'lc.tenant';

/** Build client options; omits the token entirely when signed out. */
export function options(token: string | null): AuthOptions {
  return token === null ? { baseUrl: API_BASE } : { baseUrl: API_BASE, sessionToken: token };
}

export interface AuthState {
  readonly status: 'loading' | 'signed-out' | 'signed-in';
  readonly user: AuthUser | null;
  readonly token: string | null;
  readonly memberships: readonly Membership[];
  readonly activeTenantId: string | null;
  readonly activeRole: string | null;
  signIn(email: string, password: string): Promise<LoginOutcome>;
  completeMfa(mfaToken: string, code: string): Promise<void>;
  completeRecovery(mfaToken: string, recoveryCode: string): Promise<void>;
  signOut(): Promise<void>;
  selectTenant(tenantId: string): void;
  refresh(): Promise<void>;
}

const AuthContext = createContext<AuthState | null>(null);

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext);
  if (ctx === null) {
    throw new Error('useAuth must be used inside <AuthProvider>');
  }
  return ctx;
}

function readStored(key: string): string | null {
  try {
    return window.sessionStorage.getItem(key);
  } catch {
    return null;
  }
}

function writeStored(key: string, value: string | null): void {
  try {
    if (value === null) {
      window.sessionStorage.removeItem(key);
    } else {
      window.sessionStorage.setItem(key, value);
    }
  } catch {
    /* storage unavailable (private mode); session stays in memory only */
  }
}

export function AuthProvider({ children }: { readonly children: ReactNode }): JSX.Element {
  const [token, setToken] = useState<string | null>(() => readStored(TOKEN_KEY));
  const [user, setUser] = useState<AuthUser | null>(null);
  const [memberships, setMemberships] = useState<readonly Membership[]>([]);
  const [activeTenantId, setActiveTenantId] = useState<string | null>(() => readStored(TENANT_KEY));
  const [status, setStatus] = useState<AuthState['status']>(token === null ? 'signed-out' : 'loading');

  const applySession = useCallback((session: SessionResult) => {
    setToken(session.sessionToken);
    writeStored(TOKEN_KEY, session.sessionToken);
    setUser(session.user);
    setStatus('signed-in');
  }, []);

  const clear = useCallback(() => {
    setToken(null);
    writeStored(TOKEN_KEY, null);
    setActiveTenantId(null);
    writeStored(TENANT_KEY, null);
    setUser(null);
    setMemberships([]);
    setStatus('signed-out');
  }, []);

  const refresh = useCallback(async (): Promise<void> => {
    if (token === null) {
      setStatus('signed-out');
      return;
    }
    try {
      const [current, tenants] = await Promise.all([
        apiMe(options(token)),
        myMemberships(options(token)),
      ]);
      setUser(current);
      setMemberships(tenants);
      setStatus('signed-in');
      // Keep the active tenant valid; fall back to the first membership.
      setActiveTenantId((previous) => {
        const stillValid = previous !== null && tenants.some((t) => t.tenantId === previous);
        const next = stillValid ? previous : (tenants[0]?.tenantId ?? null);
        writeStored(TENANT_KEY, next);
        return next;
      });
    } catch {
      clear(); // expired or revoked session
    }
  }, [token, clear]);

  useEffect(() => {
    void refresh();
    // Re-run only when the token identity changes.
  }, [refresh]);

  const signIn = useCallback(async (email: string, password: string): Promise<LoginOutcome> => {
    const outcome = await apiLogin(options(null), email, password);
    if (outcome.kind === 'session') {
      applySession(outcome.session);
    }
    return outcome;
  }, [applySession]);

  const completeMfa = useCallback(async (mfaToken: string, code: string): Promise<void> => {
    applySession(await apiVerifyMfa(options(null), mfaToken, code));
  }, [applySession]);

  const completeRecovery = useCallback(async (mfaToken: string, recoveryCode: string): Promise<void> => {
    applySession(await apiRecoverMfa(options(null), mfaToken, recoveryCode));
  }, [applySession]);

  const signOut = useCallback(async (): Promise<void> => {
    if (token !== null) {
      try {
        await apiLogout(options(token));
      } catch {
        /* revoking server-side is best-effort; local state clears regardless */
      }
    }
    clear();
  }, [token, clear]);

  const selectTenant = useCallback((tenantId: string): void => {
    setActiveTenantId(tenantId);
    writeStored(TENANT_KEY, tenantId);
  }, []);

  const activeRole = useMemo(
    () => memberships.find((m) => m.tenantId === activeTenantId)?.role ?? null,
    [memberships, activeTenantId],
  );

  const value = useMemo<AuthState>(() => ({
    status, user, token, memberships, activeTenantId, activeRole,
    signIn, completeMfa, completeRecovery, signOut, selectTenant, refresh,
  }), [status, user, token, memberships, activeTenantId, activeRole,
    signIn, completeMfa, completeRecovery, signOut, selectTenant, refresh]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}
