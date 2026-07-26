// Typed client for the platform (super-admin) REST API (P6). Disjoint from tenant
// authorization: these call /api/platform/* and are gated by the caller's PLATFORM
// roles (never tenant membership). Uses the same bearer (session) as the rest of the
// console — reusing ControlPlaneOptions.
import { errorFrom } from './index';
import type { ControlPlaneOptions, Tenant } from './control-plane';

/** A platform (super-admin) role assignment. */
export interface PlatformRoleAssignment {
  readonly id: string;
  readonly userId: string;
  readonly role: string;
  readonly grantedByUserId: string;
  readonly reason: string | null;
  readonly createdAt: string;
  readonly expiresAt: string | null;
  readonly active: boolean;
}

/** A time-limited tenant support-access grant. */
export interface PlatformSupportGrant {
  readonly id: string;
  readonly subjectTenantId: string;
  readonly requesterUserId: string;
  readonly reason: string;
  readonly status: string;
  readonly createdAt: string;
  readonly expiresAt: string | null;
  readonly decidedByUserId: string | null;
  readonly active: boolean;
}

/** A two-party tenant offboarding request. */
export interface PlatformOffboardRequest {
  readonly id: string;
  readonly subjectTenantId: string;
  readonly requesterUserId: string;
  readonly reason: string;
  readonly status: string;
  readonly createdAt: string;
  readonly decidedByUserId: string | null;
}

/** An append-only platform security/audit event. */
export interface PlatformSecurityEvent {
  readonly id: string;
  readonly at: string;
  readonly kind: string;
  readonly actorUserId: string;
  readonly detail: string;
}

function authHeaders(opts: ControlPlaneOptions): HeadersInit {
  return { Authorization: `Bearer ${opts.adminToken}` };
}

async function req(
  opts: ControlPlaneOptions,
  method: string,
  path: string,
  body?: unknown,
): Promise<Response> {
  const f = opts.fetchImpl ?? fetch;
  const headers: Record<string, string> = { ...(authHeaders(opts) as Record<string, string>) };
  const init: RequestInit = { method, headers };
  if (body !== undefined) {
    headers['Content-Type'] = 'application/json';
    init.body = JSON.stringify(body);
  }
  const res = await f(new URL(path, opts.baseUrl), init);
  if (!res.ok) {
    throw await errorFrom(res, path);
  }
  return res;
}

async function reqJson<T>(
  opts: ControlPlaneOptions,
  method: string,
  path: string,
  body?: unknown,
): Promise<T> {
  return (await (await req(opts, method, path, body)).json()) as T;
}

const enc = encodeURIComponent;

/** The caller's own platform roles (empty ⇒ not a platform operator). */
export function platformWhoami(opts: ControlPlaneOptions): Promise<{ readonly roles: readonly string[] }> {
  return reqJson(opts, 'GET', '/api/platform/whoami');
}

// ── Platform role administration (Root Owner) ──────────────────────────────
export function listPlatformRoleAssignments(
  opts: ControlPlaneOptions,
  userId?: string,
): Promise<readonly PlatformRoleAssignment[]> {
  const q = userId ? `?userId=${enc(userId)}` : '';
  return reqJson<PlatformRoleAssignment[]>(opts, 'GET', `/api/platform/role-assignments${q}`);
}

export function grantPlatformRole(
  opts: ControlPlaneOptions,
  grant: { readonly userId: string; readonly role: string; readonly expiresAt?: string; readonly reason?: string },
): Promise<PlatformRoleAssignment> {
  return reqJson<PlatformRoleAssignment>(opts, 'POST', '/api/platform/role-assignments', grant);
}

export async function revokePlatformRole(opts: ControlPlaneOptions, assignmentId: string): Promise<void> {
  await req(opts, 'DELETE', `/api/platform/role-assignments/${enc(assignmentId)}`);
}

// ── Tenant registry + lifecycle ────────────────────────────────────────────
export function listPlatformTenants(opts: ControlPlaneOptions): Promise<readonly Tenant[]> {
  return reqJson<Tenant[]>(opts, 'GET', '/api/platform/tenants');
}

export function provisionTenant(opts: ControlPlaneOptions, name: string): Promise<Tenant> {
  return reqJson<Tenant>(opts, 'POST', '/api/platform/tenants', { name });
}

export async function suspendTenant(opts: ControlPlaneOptions, tenantId: string): Promise<void> {
  await req(opts, 'POST', `/api/platform/tenants/${enc(tenantId)}/suspend`);
}

export async function reactivatePlatformTenant(opts: ControlPlaneOptions, tenantId: string): Promise<void> {
  await req(opts, 'POST', `/api/platform/tenants/${enc(tenantId)}/reactivate`);
}

/** Complete offboarding into the terminal archived state (only from the offboarding state). */
export async function archiveTenant(opts: ControlPlaneOptions, tenantId: string): Promise<void> {
  await req(opts, 'POST', `/api/platform/tenants/${enc(tenantId)}/archive`);
}

/** Cancel offboarding during cooling-off, returning the tenant to active. */
export async function cancelTenantOffboarding(opts: ControlPlaneOptions, tenantId: string): Promise<void> {
  await req(opts, 'POST', `/api/platform/tenants/${enc(tenantId)}/cancel-offboarding`);
}

/** Place or lift a legal hold, which overrides archiving/deletion. */
export async function setTenantLegalHold(opts: ControlPlaneOptions, tenantId: string, hold: boolean): Promise<void> {
  await req(opts, 'POST', `/api/platform/tenants/${enc(tenantId)}/legal-hold`, { hold });
}

/** A tenant's exported control-plane data (§10.3). Shape mirrors the server's TenantExport:
 *  the tenant record, its people (members + open invitations), its subscription, its fleet
 *  + config, and its audit trail. */
export interface TenantExportArtifact {
  readonly tenant: Tenant;
  readonly members: readonly unknown[];
  readonly invitations: readonly unknown[];
  readonly subscription: unknown | null;
  readonly gateways: readonly unknown[];
  readonly configs: readonly unknown[];
  readonly audit: readonly unknown[];
  readonly exportedAt: string;
}

/** Export a tenant's control-plane data (fleet, config, audit) as an artifact. */
export function exportTenant(opts: ControlPlaneOptions, tenantId: string): Promise<TenantExportArtifact> {
  return reqJson<TenantExportArtifact>(opts, 'GET', `/api/platform/tenants/${enc(tenantId)}/export`);
}

export function setTenantSubscription(
  opts: ControlPlaneOptions,
  tenantId: string,
  planId: string,
): Promise<unknown> {
  return reqJson(opts, 'POST', `/api/platform/tenants/${enc(tenantId)}/subscription`, { planId });
}

// ── Support access (Support requests, Security approves) ───────────────────
export function listSupportGrants(opts: ControlPlaneOptions): Promise<readonly PlatformSupportGrant[]> {
  return reqJson<PlatformSupportGrant[]>(opts, 'GET', '/api/platform/support-grants');
}

export function requestSupportGrant(
  opts: ControlPlaneOptions,
  request: { readonly subjectTenantId: string; readonly reason?: string; readonly durationMinutes?: number },
): Promise<PlatformSupportGrant> {
  return reqJson<PlatformSupportGrant>(opts, 'POST', '/api/platform/support-grants', request);
}

export async function approveSupportGrant(opts: ControlPlaneOptions, id: string): Promise<void> {
  await req(opts, 'POST', `/api/platform/support-grants/${enc(id)}/approve`);
}

export async function rejectSupportGrant(opts: ControlPlaneOptions, id: string): Promise<void> {
  await req(opts, 'POST', `/api/platform/support-grants/${enc(id)}/reject`);
}

// ── Tenant offboarding (two-party) ─────────────────────────────────────────
export function listOffboardRequests(opts: ControlPlaneOptions): Promise<readonly PlatformOffboardRequest[]> {
  return reqJson<PlatformOffboardRequest[]>(opts, 'GET', '/api/platform/offboard-requests');
}

export function requestOffboard(
  opts: ControlPlaneOptions,
  request: { readonly subjectTenantId: string; readonly reason?: string },
): Promise<PlatformOffboardRequest> {
  return reqJson<PlatformOffboardRequest>(opts, 'POST', '/api/platform/offboard-requests', request);
}

export async function approveOffboard(opts: ControlPlaneOptions, id: string): Promise<void> {
  await req(opts, 'POST', `/api/platform/offboard-requests/${enc(id)}/approve`);
}

export async function rejectOffboard(opts: ControlPlaneOptions, id: string): Promise<void> {
  await req(opts, 'POST', `/api/platform/offboard-requests/${enc(id)}/reject`);
}

// ── Security / audit events ────────────────────────────────────────────────
export function listSecurityEvents(
  opts: ControlPlaneOptions,
  limit = 100,
): Promise<readonly PlatformSecurityEvent[]> {
  const clamped = Math.max(1, Math.min(500, Math.trunc(limit)));
  return reqJson<PlatformSecurityEvent[]>(opts, 'GET', `/api/platform/security-events?limit=${clamped}`);
}
