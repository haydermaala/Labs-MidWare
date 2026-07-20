// Typed client for the control-plane REST API (admin bearer token).
// Fleet management: tenants, gateway inventory with liveness, and lifecycle
// actions. No PHI or result values cross this surface.
import { ApiError } from './index';

/** Derived liveness label for a gateway (mirrors the server's GatewayLiveness). */
export type GatewayStatusLabel = 'never' | 'online' | 'offline' | 'decommissioned';

/** A tenant (customer/organization). Inactive tenants cannot enroll gateways. */
export interface Tenant {
  readonly id: string;
  readonly name: string;
  readonly createdAt: string;
  readonly active: boolean;
}

/** A gateway in the fleet view (no credential; liveness derived server-side). */
export interface GatewaySummary {
  readonly id: string;
  readonly tenantId: string;
  readonly name: string;
  readonly enrolledAt: string;
  readonly active: boolean;
  readonly lastSeenAt: string | null;
  readonly status: GatewayStatusLabel;
}

/** A one-time bootstrap token an operator hands to a gateway to enroll. */
export interface BootstrapToken {
  readonly token: string;
  readonly expiresAt: string;
}

/** An append-only audit event. */
export interface AuditEvent {
  readonly at: string;
  readonly kind: string;
  readonly tenantId: string;
  readonly detail: string;
}

/** Options for the control-plane client. */
export interface ControlPlaneOptions {
  /** Base URL of the control-plane API, e.g. "https://…up.railway.app". */
  readonly baseUrl: string;
  /** Admin bearer token; required for every management call. */
  readonly adminToken: string;
  /** Injectable fetch (for tests). */
  readonly fetchImpl?: typeof fetch;
}

function authHeaders(opts: ControlPlaneOptions): HeadersInit {
  return { Authorization: `Bearer ${opts.adminToken}` };
}

async function request(
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
    throw new ApiError(res.status, `${method} ${path} failed: ${res.status}`);
  }
  return res;
}

async function requestJson<T>(
  opts: ControlPlaneOptions,
  method: string,
  path: string,
  body?: unknown,
): Promise<T> {
  const res = await request(opts, method, path, body);
  return (await res.json()) as T;
}

/** All tenants (admin). */
export function listTenants(opts: ControlPlaneOptions): Promise<readonly Tenant[]> {
  return requestJson<Tenant[]>(opts, 'GET', '/api/tenants');
}

/** Create a tenant (admin). */
export function createTenant(opts: ControlPlaneOptions, name: string): Promise<Tenant> {
  return requestJson<Tenant>(opts, 'POST', '/api/tenants', { name });
}

/** Deactivate a tenant (soft; stops new enrollment, retains data/audit). */
export async function deactivateTenant(opts: ControlPlaneOptions, tenantId: string): Promise<void> {
  await request(opts, 'POST', `/api/tenants/${encodeURIComponent(tenantId)}/deactivate`);
}

/** Reactivate a previously deactivated tenant. */
export async function reactivateTenant(opts: ControlPlaneOptions, tenantId: string): Promise<void> {
  await request(opts, 'POST', `/api/tenants/${encodeURIComponent(tenantId)}/reactivate`);
}

/** Issue a one-time bootstrap token for a tenant (admin). */
export function issueEnrollmentToken(
  opts: ControlPlaneOptions,
  tenantId: string,
): Promise<BootstrapToken> {
  return requestJson<BootstrapToken>(
    opts,
    'POST',
    `/api/tenants/${encodeURIComponent(tenantId)}/enrollment-tokens`,
  );
}

/** Gateways for a tenant, with derived liveness (tenant-scoped). */
export function listGateways(
  opts: ControlPlaneOptions,
  tenantId: string,
): Promise<readonly GatewaySummary[]> {
  return requestJson<GatewaySummary[]>(
    opts,
    'GET',
    `/api/tenants/${encodeURIComponent(tenantId)}/gateways`,
  );
}

/** Decommission a gateway: mark inactive and revoke its credential. */
export async function decommissionGateway(
  opts: ControlPlaneOptions,
  tenantId: string,
  gatewayId: string,
): Promise<void> {
  await request(
    opts,
    'POST',
    `/api/tenants/${encodeURIComponent(tenantId)}/gateways/${encodeURIComponent(gatewayId)}/decommission`,
  );
}

/** Append-only audit events for a tenant (admin). */
export function getAudit(
  opts: ControlPlaneOptions,
  tenantId: string,
): Promise<readonly AuditEvent[]> {
  return requestJson<AuditEvent[]>(
    opts,
    'GET',
    `/api/tenants/${encodeURIComponent(tenantId)}/audit`,
  );
}

/** A member of a tenant (admin view). */
export interface Member {
  readonly userId: string;
  readonly email: string;
  readonly role: string;
  readonly since: string;
  readonly active: boolean;
}

/** Members of a tenant (requires user-management permission). */
export function listMembers(
  opts: ControlPlaneOptions,
  tenantId: string,
): Promise<readonly Member[]> {
  return requestJson<Member[]>(opts, 'GET', `/api/tenants/${encodeURIComponent(tenantId)}/members`);
}

/** Change a member's role. Owner grants/revocations require an owner actor. */
export async function changeMemberRole(
  opts: ControlPlaneOptions,
  tenantId: string,
  userId: string,
  role: string,
): Promise<void> {
  await request(
    opts, 'POST',
    `/api/tenants/${encodeURIComponent(tenantId)}/members/${encodeURIComponent(userId)}/role`,
    { role },
  );
}

/** Remove a member (soft: membership deactivated, history retained). */
export async function removeMember(
  opts: ControlPlaneOptions,
  tenantId: string,
  userId: string,
): Promise<void> {
  await request(
    opts, 'POST',
    `/api/tenants/${encodeURIComponent(tenantId)}/members/${encodeURIComponent(userId)}/remove`,
  );
}

/** Result of creating an invitation, including whether its email was delivered. */
export interface InvitationCreated {
  readonly invitation: InvitationView;
  readonly emailDelivered: boolean;
}

/** Invite a user into a tenant with a role. Invitation creation is durable even
 * if delivery fails; the returned flag reports whether the email was accepted. */
export function inviteMember(
  opts: ControlPlaneOptions,
  tenantId: string,
  email: string,
  role: string,
): Promise<InvitationCreated> {
  return requestJson<InvitationCreated>(
    opts, 'POST', `/api/tenants/${encodeURIComponent(tenantId)}/invitations`, { email, role });
}

/** Pending/handled invitations for a tenant. */
export function listInvitations(
  opts: ControlPlaneOptions,
  tenantId: string,
): Promise<readonly InvitationView[]> {
  return requestJson<InvitationView[]>(
    opts, 'GET', `/api/tenants/${encodeURIComponent(tenantId)}/invitations`);
}

/** Revoke a pending invitation. */
export async function revokeInvitation(
  opts: ControlPlaneOptions,
  tenantId: string,
  invitationId: string,
): Promise<void> {
  await request(
    opts, 'POST',
    `/api/tenants/${encodeURIComponent(tenantId)}/invitations/${encodeURIComponent(invitationId)}/revoke`,
  );
}

/** An invitation as shown to administrators (never the token). */
export interface InvitationView {
  readonly id: string;
  readonly email: string;
  readonly role: string;
  readonly expiresAt: string;
  readonly status: 'pending' | 'accepted' | 'revoked' | 'expired';
}
