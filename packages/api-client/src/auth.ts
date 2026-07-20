// Typed client for the LabConnect identity API (Phase C surface): sessions,
// MFA challenge flow, account email flows, memberships, and invitations.
// The SPA keeps the session token in memory and sends Bearer; the API also
// sets an HttpOnly cookie for same-origin browsing (Phase H).
import { ApiError } from './index';

/** Public view of the signed-in user (no secrets ever cross this surface). */
export interface AuthUser {
  readonly id: string;
  readonly email: string;
  readonly createdAt: string;
  readonly emailVerified: boolean;
  readonly active: boolean;
  readonly mfaEnabled: boolean;
}

/** A session issued at login/MFA completion (token shown once). */
export interface SessionResult {
  readonly sessionToken: string;
  readonly expiresAt: string;
  readonly user: AuthUser;
}

/** Login either yields a session or an MFA challenge to complete. */
export type LoginOutcome =
  | { readonly kind: 'session'; readonly session: SessionResult }
  | { readonly kind: 'mfa'; readonly mfaToken: string };

/** A membership row (drives the tenant switcher). */
export interface Membership {
  readonly tenantId: string;
  readonly tenantName: string;
  readonly role: string;
  readonly tenantActive: boolean;
}

export interface AuthOptions {
  readonly baseUrl: string;
  /** Session token for authenticated calls (omit for anonymous flows). */
  readonly sessionToken?: string;
  readonly fetchImpl?: typeof fetch;
}

async function call(
  opts: AuthOptions,
  method: string,
  path: string,
  body?: unknown,
): Promise<Response> {
  const f = opts.fetchImpl ?? fetch;
  const headers: Record<string, string> = {};
  if (opts.sessionToken) {
    headers.Authorization = `Bearer ${opts.sessionToken}`;
  }
  const init: RequestInit = { method, headers };
  if (body !== undefined) {
    headers['Content-Type'] = 'application/json';
    init.body = JSON.stringify(body);
  }
  const res = await f(new URL(path, opts.baseUrl), init);
  if (!res.ok) {
    throw new ApiError(res.status, `${method} ${path} failed: ${res.status}`);
  }
  return res;
}

async function json<T>(opts: AuthOptions, method: string, path: string, body?: unknown): Promise<T> {
  return (await (await call(opts, method, path, body)).json()) as T;
}

/** Password login; discriminates the MFA challenge from a plain session. */
export async function login(opts: AuthOptions, email: string, password: string): Promise<LoginOutcome> {
  const raw = await json<{ mfaRequired?: boolean; mfaToken?: string } & Partial<SessionResult>>(
    opts, 'POST', '/api/auth/login', { email, password });
  if (raw.mfaRequired === true && typeof raw.mfaToken === 'string') {
    return { kind: 'mfa', mfaToken: raw.mfaToken };
  }
  return { kind: 'session', session: raw as SessionResult };
}

/** Complete an MFA challenge with an authenticator code. */
export function verifyMfa(opts: AuthOptions, mfaToken: string, code: string): Promise<SessionResult> {
  return json<SessionResult>(opts, 'POST', '/api/auth/mfa/verify', { mfaToken, code });
}

/** Complete an MFA challenge with a single-use recovery code. */
export function recoverMfa(opts: AuthOptions, mfaToken: string, recoveryCode: string): Promise<SessionResult> {
  return json<SessionResult>(opts, 'POST', '/api/auth/mfa/recover', { mfaToken, recoveryCode });
}

export function me(opts: AuthOptions): Promise<AuthUser> {
  return json<AuthUser>(opts, 'GET', '/api/auth/me');
}

export async function logout(opts: AuthOptions): Promise<void> {
  await call(opts, 'POST', '/api/auth/logout');
}

/** Always resolves regardless of account existence (server is oracle-free). */
export async function forgotPassword(opts: AuthOptions, email: string): Promise<void> {
  await call(opts, 'POST', '/api/auth/forgot-password', { email });
}

export async function resetPassword(opts: AuthOptions, token: string, newPassword: string): Promise<void> {
  await call(opts, 'POST', '/api/auth/reset-password', { token, newPassword });
}

export async function sendVerification(opts: AuthOptions): Promise<void> {
  await call(opts, 'POST', '/api/auth/send-verification');
}

export async function verifyEmail(opts: AuthOptions, token: string): Promise<void> {
  await call(opts, 'POST', '/api/auth/verify-email', { token });
}

/** The signed-in user's tenants (tenant switcher). */
export function myMemberships(opts: AuthOptions): Promise<readonly Membership[]> {
  return json<Membership[]>(opts, 'GET', '/api/me/memberships');
}

/** Accept an emailed invitation as the signed-in user. */
export function acceptInvitation(opts: AuthOptions, token: string): Promise<Membership> {
  return json<Membership>(opts, 'POST', '/api/invitations/accept', { token });
}

/** MFA enrollment material (secret shown once; URI for authenticator apps). */
export interface MfaSetup {
  readonly secret: string;
  readonly provisioningUri: string;
}

/** Begin MFA enrollment: returns the pending secret to load into an app. */
export function setupMfa(opts: AuthOptions): Promise<MfaSetup> {
  return json<MfaSetup>(opts, 'POST', '/api/auth/mfa/setup');
}

/** Arm MFA by proving a current code; returns recovery codes shown ONCE. */
export async function enableMfa(opts: AuthOptions, code: string): Promise<readonly string[]> {
  const raw = await json<{ recoveryCodes: string[] }>(opts, 'POST', '/api/auth/mfa/enable', { code });
  return raw.recoveryCodes;
}

/** Disable MFA (requires a current code); burns all recovery codes. */
export async function disableMfa(opts: AuthOptions, code: string): Promise<void> {
  await call(opts, 'POST', '/api/auth/mfa/disable', { code });
}

/** An active session for the signed-in user (never the token). */
export interface SessionInfo {
  readonly id: string;
  readonly createdAt: string;
  readonly expiresAt: string;
  readonly lastSeenAt: string;
  readonly current: boolean;
}

/** The signed-in user's active sessions, current one marked. */
export function listSessions(opts: AuthOptions): Promise<readonly SessionInfo[]> {
  return json<SessionInfo[]>(opts, 'GET', '/api/auth/sessions');
}

/** Revoke every session (including this one). Returns the count revoked. */
export async function revokeAllSessions(opts: AuthOptions): Promise<number> {
  const raw = await json<{ revoked: number }>(opts, 'POST', '/api/auth/sessions/revoke-all');
  return raw.revoked;
}

// --- billing (Phase E) -----------------------------------------------------
// Entitlement scope only; the catalog carries no prices (the pricing gate).

/** A published plan tier. `gatewayQuota` of -1 means unlimited. */
export interface BillingPlan {
  readonly id: string;
  readonly name: string;
  readonly gatewayQuota: number;
  readonly features: readonly string[];
}

/** Server-computed entitlements for the active tenant. */
export interface Entitlements {
  readonly planId: string;
  readonly planName: string;
  readonly status: string;
  readonly gatewayQuota: number;
  readonly features: readonly string[];
  readonly currentPeriodEnd: string | null;
  readonly cancelAtPeriodEnd: boolean;
}

/** A tenant's subscription view (never provider secrets or card data). */
export interface SubscriptionView {
  readonly planId: string;
  readonly status: string;
  readonly currentPeriodEnd: string | null;
  readonly cancelAtPeriodEnd: boolean;
}

/** The tenant's entitlements plus its subscription (null before first checkout). */
export interface TenantBilling {
  readonly entitlements: Entitlements;
  readonly subscription: SubscriptionView | null;
}

/** The public plan catalog (no prices). Anonymous — no session needed. */
export function billingPlans(opts: AuthOptions): Promise<readonly BillingPlan[]> {
  return json<BillingPlan[]>(opts, 'GET', '/api/billing/plans');
}

/** The active tenant's entitlements and subscription. Any member may read. */
export function tenantBilling(opts: AuthOptions, tenantId: string): Promise<TenantBilling> {
  return json<TenantBilling>(opts, 'GET', `/api/tenants/${tenantId}/billing`);
}
