// Platform (super-admin) console (P6). Gated by the caller's PLATFORM roles, disjoint
// from tenant membership. Surfaces: the tenant registry/lifecycle, time-boxed support
// access (request → distinct-party approve), two-party tenant offboarding, an append-only
// security-event log, and platform-role administration. Each section is shown only to the
// roles that can use it. The server is the authority on every gate; the UI mirrors the
// reasons and prompts for step-up (MFA / fresh re-auth) when the server requires it.

import { useCallback, useEffect, useState } from 'react';
import {
  approveOffboard, approveSupportGrant, grantPlatformRole, listOffboardRequests,
  listPlatformRoleAssignments, listPlatformTenants, listSecurityEvents, listSupportGrants,
  provisionTenant, reactivatePlatformTenant, rejectOffboard, rejectSupportGrant,
  requestOffboard, requestSupportGrant, revokePlatformRole, suspendTenant,
  type ControlPlaneOptions, type PlatformOffboardRequest, type PlatformRoleAssignment,
  type PlatformSecurityEvent, type PlatformSupportGrant, type Tenant,
} from '@lab-connect/api-client';
import { Button, Field, color, fontSize, space } from '@lab-connect/ui';
import { API_BASE } from '../config';
import { useAuth } from '../auth/AuthProvider';
import { StepUpCancelledError, useStepUp } from '../auth/StepUpProvider';
import { usePlatformRoles } from '../platform/usePlatformRoles';
import { PageHeader } from './Pages';

const PLATFORM_ROLES = [
  'platform-root-owner', 'platform-operations-admin', 'platform-support-engineer',
  'platform-billing-admin', 'platform-security-admin', 'platform-auditor', 'platform-release-manager',
] as const;

function opts(token: string): ControlPlaneOptions {
  return { baseUrl: API_BASE, adminToken: token };
}

function fmtDate(instant: string): string {
  const d = new Date(instant);
  return Number.isNaN(d.getTime()) ? instant : d.toISOString().slice(0, 10);
}

const th: React.CSSProperties = {
  textAlign: 'left', padding: `${space[2]}px ${space[3]}px`, fontSize: fontSize.meta,
  fontWeight: 600, color: color.fgMuted, borderBottom: `1px solid ${color.border}`, whiteSpace: 'nowrap',
};
const td: React.CSSProperties = {
  padding: `${space[2]}px ${space[3]}px`, fontSize: fontSize.table,
  borderBottom: `1px solid ${color.border}`, verticalAlign: 'middle',
};

export function PlatformPage(): JSX.Element {
  const { token } = useAuth();
  const { guard } = useStepUp();
  const platform = usePlatformRoles();
  const isRoot = platform.has('platform-root-owner');
  const canManageRoles = isRoot;
  const canManageTenants = isRoot || platform.has('platform-operations-admin');
  const canApproveSupport = isRoot || platform.has('platform-security-admin');
  const canRequestSupport = isRoot || platform.has('platform-support-engineer');
  const canOffboard = isRoot || platform.has('platform-operations-admin');
  const canReadSecurity = isRoot || platform.has('platform-security-admin') || platform.has('platform-auditor');

  const [assignments, setAssignments] = useState<readonly PlatformRoleAssignment[]>([]);
  const [tenants, setTenants] = useState<readonly Tenant[]>([]);
  const [supportGrants, setSupportGrants] = useState<readonly PlatformSupportGrant[]>([]);
  const [offboards, setOffboards] = useState<readonly PlatformOffboardRequest[]>([]);
  const [events, setEvents] = useState<readonly PlatformSecurityEvent[]>([]);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  const load = useCallback(async (): Promise<void> => {
    if (token === null || !platform.hasAccess) {
      return;
    }
    const o = opts(token);
    // Each read is gated by its own permission; a role that lacks one just gets an
    // empty section rather than failing the whole page.
    const [a, t, s, ob, ev] = await Promise.all([
      canManageRoles ? listPlatformRoleAssignments(o).catch(() => []) : Promise.resolve([]),
      listPlatformTenants(o).catch(() => []),
      canApproveSupport ? listSupportGrants(o).catch(() => []) : Promise.resolve([]),
      canOffboard ? listOffboardRequests(o).catch(() => []) : Promise.resolve([]),
      canReadSecurity ? listSecurityEvents(o, 50).catch(() => []) : Promise.resolve([]),
    ]);
    setAssignments(a);
    setTenants(t);
    setSupportGrants(s);
    setOffboards(ob);
    setEvents(ev);
  }, [token, platform.hasAccess, canManageRoles, canApproveSupport, canOffboard, canReadSecurity]);

  useEffect(() => { void load(); }, [load]);

  async function run(key: string, action: () => Promise<void>): Promise<void> {
    setBusy(key);
    setNotice(null);
    try {
      await guard(action);
      await load();
    } catch (e) {
      if (e instanceof StepUpCancelledError) {
        return;
      }
      const err = e as { status?: number; reason?: string };
      setNotice(err.reason ?? 'That action could not be completed. Please try again.');
    } finally {
      setBusy(null);
    }
  }

  if (platform.loading) {
    return (
      <>
        <PageHeader title="Platform" description="Super-admin operations." />
        <div aria-hidden="true" style={{ display: 'grid', gap: space[2] }}>
          {[0, 1, 2].map((i) => <div key={i} style={{ height: 36, borderRadius: 4, background: color.surface2 }} />)}
        </div>
      </>
    );
  }

  if (!platform.hasAccess) {
    return (
      <>
        <PageHeader title="Platform" description="Super-admin operations." />
        <p role="alert" style={{ margin: 0, padding: space[4], borderRadius: 6, background: color.surface1,
          border: `1px solid ${color.border}`, color: color.fgMuted }}>
          You do not hold a platform role. This console is for platform operators; a platform role is
          distinct from any tenant membership.
        </p>
      </>
    );
  }

  return (
    <>
      <PageHeader
        title="Platform"
        description={`Super-admin operations. Your roles: ${platform.roles.join(', ')}.`}
      />

      {notice !== null && (
        <p role="alert" style={{
          margin: `0 0 ${space[4]}px`, padding: `${space[2]}px ${space[3]}px`, borderRadius: 4,
          color: color.danger, border: `1px solid ${color.danger}`,
          background: 'color-mix(in oklch, var(--lc-danger) 8%, transparent)', fontSize: fontSize.body,
        }}>{notice}</p>
      )}

      <div style={{ display: 'grid', gap: space[5] }}>
        <TenantsSection
          tenants={tenants}
          canManage={canManageTenants}
          busy={busy}
          onProvision={(name) => run('provision', async () => { await provisionTenant(opts(token!), name); })}
          onSuspend={(id) => run(`suspend-${id}`, () => suspendTenant(opts(token!), id))}
          onReactivate={(id) => run(`react-${id}`, () => reactivatePlatformTenant(opts(token!), id))}
        />

        {(canRequestSupport || canApproveSupport) && (
          <SupportSection
            grants={supportGrants}
            tenants={tenants}
            canRequest={canRequestSupport}
            canApprove={canApproveSupport}
            busy={busy}
            onRequest={(subjectTenantId, reason) => run('support-request', async () => {
              await requestSupportGrant(opts(token!), { subjectTenantId, ...(reason ? { reason } : {}) });
            })}
            onApprove={(id) => run(`support-approve-${id}`, () => approveSupportGrant(opts(token!), id))}
            onReject={(id) => run(`support-reject-${id}`, () => rejectSupportGrant(opts(token!), id))}
          />
        )}

        {canOffboard && (
          <OffboardSection
            requests={offboards}
            tenants={tenants}
            busy={busy}
            onRequest={(subjectTenantId, reason) => run('offboard-request', async () => {
              await requestOffboard(opts(token!), { subjectTenantId, ...(reason ? { reason } : {}) });
            })}
            onApprove={(id) => run(`offboard-approve-${id}`, () => approveOffboard(opts(token!), id))}
            onReject={(id) => run(`offboard-reject-${id}`, () => rejectOffboard(opts(token!), id))}
          />
        )}

        {canReadSecurity && <SecuritySection events={events} />}

        {canManageRoles && (
          <RolesSection
            assignments={assignments}
            busy={busy}
            onGrant={(userId, role, reason) => run('grant', async () => {
              await grantPlatformRole(opts(token!), { userId, role, ...(reason ? { reason } : {}) });
            })}
            onRevoke={(id) => run(`revoke-${id}`, () => revokePlatformRole(opts(token!), id))}
          />
        )}
      </div>
    </>
  );
}

/** A short chip for pending/approved/rejected/expired status labels. */
function StatusChip({ status }: { readonly status: string }): JSX.Element {
  const tone =
    status === 'approved' || status === 'active' ? color.ok
    : status === 'rejected' || status === 'expired' ? color.danger
    : color.warn;
  return (
    <span style={{
      display: 'inline-block', padding: `2px ${space[2]}px`, borderRadius: 999,
      fontSize: fontSize.meta, fontWeight: 600, color: tone,
      border: `1px solid ${tone}`, background: 'transparent', whiteSpace: 'nowrap',
    }}>{status}</span>
  );
}

/** A tenant picker built from the tenant registry, so requesters pick by name not id. */
function TenantSelect({ id, tenants, value, onChange }: {
  readonly id: string;
  readonly tenants: readonly Tenant[];
  readonly value: string;
  readonly onChange: (v: string) => void;
}): JSX.Element {
  return (
    <div className="lc-field" style={{ flex: '1 1 240px' }}>
      <label className="lc-field__label" htmlFor={id}>Tenant</label>
      <select id={id} className="lc-input" value={value} onChange={(e) => onChange(e.target.value)} required>
        <option value="" disabled>Select a tenant…</option>
        {tenants.map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
      </select>
    </div>
  );
}

function TenantsSection({ tenants, canManage, busy, onProvision, onSuspend, onReactivate }: {
  readonly tenants: readonly Tenant[];
  readonly canManage: boolean;
  readonly busy: string | null;
  readonly onProvision: (name: string) => Promise<void>;
  readonly onSuspend: (id: string) => Promise<void>;
  readonly onReactivate: (id: string) => Promise<void>;
}): JSX.Element {
  const [name, setName] = useState('');
  return (
    <section style={{ display: 'grid', gap: space[3] }}>
      <h2 style={{ fontSize: fontSize.section, fontWeight: 600 }}>
        Tenants <span style={{ color: color.fgMuted, fontWeight: 400 }}>({tenants.length})</span>
      </h2>

      {canManage && (
        <form
          className="lc-card"
          style={{ padding: space[4], display: 'flex', gap: space[3], alignItems: 'end', flexWrap: 'wrap' }}
          onSubmit={(e) => { e.preventDefault(); void onProvision(name).then(() => setName('')); }}
        >
          <div style={{ flex: '1 1 240px' }}>
            <Field label="Provision a tenant" value={name} onChange={(e) => setName(e.target.value)}
              placeholder="Acme Laboratories" required />
          </div>
          <Button type="submit" loading={busy === 'provision'} disabled={name.trim() === ''}>Provision</Button>
        </form>
      )}

      <div className="lc-card" style={{ overflowX: 'auto' }}>
        <table style={{ borderCollapse: 'collapse', width: '100%', minWidth: 560 }}>
          <thead>
            <tr>
              <th scope="col" style={th}>Tenant</th>
              <th scope="col" style={th}>Status</th>
              <th scope="col" style={th}>Created</th>
              {canManage && <th scope="col" style={{ ...th, textAlign: 'right' }}>Actions</th>}
            </tr>
          </thead>
          <tbody>
            {tenants.map((t) => {
              const offboarded = 'offboarded' in t && (t as { offboarded?: boolean }).offboarded === true;
              const label = offboarded ? 'offboarded' : t.active ? 'active' : 'suspended';
              return (
                <tr key={t.id}>
                  <td style={td}><div style={{ fontWeight: 600 }}>{t.name}</div>
                    <div style={{ fontSize: 11, color: color.fgMuted }} className="lc-tabular">{t.id}</div></td>
                  <td style={td}>{label}</td>
                  <td style={{ ...td, whiteSpace: 'nowrap' }} className="lc-tabular">{fmtDate(t.createdAt)}</td>
                  {canManage && (
                    <td style={{ ...td, textAlign: 'right' }}>
                      {offboarded ? (
                        <span style={{ color: color.fgMuted, fontSize: fontSize.meta }}>terminal</span>
                      ) : t.active ? (
                        <Button variant="secondary" loading={busy === `suspend-${t.id}`}
                          onClick={() => void onSuspend(t.id)}>Suspend</Button>
                      ) : (
                        <Button variant="secondary" loading={busy === `react-${t.id}`}
                          onClick={() => void onReactivate(t.id)}>Reactivate</Button>
                      )}
                    </td>
                  )}
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function RolesSection({ assignments, busy, onGrant, onRevoke }: {
  readonly assignments: readonly PlatformRoleAssignment[];
  readonly busy: string | null;
  readonly onGrant: (userId: string, role: string, reason: string) => Promise<void>;
  readonly onRevoke: (id: string) => Promise<void>;
}): JSX.Element {
  const [userId, setUserId] = useState('');
  const [role, setRole] = useState<string>('platform-auditor');
  const [reason, setReason] = useState('');
  const active = assignments.filter((a) => a.active);

  return (
    <section style={{ display: 'grid', gap: space[3] }}>
      <h2 style={{ fontSize: fontSize.section, fontWeight: 600 }}>
        Platform roles <span style={{ color: color.fgMuted, fontWeight: 400 }}>({active.length} active)</span>
      </h2>

      <form
        className="lc-card"
        style={{ padding: space[4], display: 'flex', gap: space[3], alignItems: 'end', flexWrap: 'wrap' }}
        onSubmit={(e) => { e.preventDefault(); void onGrant(userId, role, reason).then(() => { setUserId(''); setReason(''); }); }}
      >
        <div style={{ flex: '1 1 200px' }}>
          <Field label="User id" value={userId} onChange={(e) => setUserId(e.target.value)}
            placeholder="usr_…" required />
        </div>
        <div className="lc-field" style={{ flex: '0 1 220px' }}>
          <label className="lc-field__label" htmlFor="platform-role">Role</label>
          <select id="platform-role" className="lc-input" value={role} onChange={(e) => setRole(e.target.value)}>
            {PLATFORM_ROLES.map((r) => <option key={r} value={r}>{r}</option>)}
          </select>
        </div>
        <div style={{ flex: '1 1 200px' }}>
          <Field label="Reason" value={reason} onChange={(e) => setReason(e.target.value)}
            placeholder="required for break-glass" />
        </div>
        <Button type="submit" loading={busy === 'grant'} disabled={userId.trim() === ''}>Grant</Button>
      </form>

      {active.length === 0 ? (
        <p style={{ margin: 0, color: color.fgMuted, fontSize: fontSize.body }}>No active platform grants.</p>
      ) : (
        <div className="lc-card" style={{ overflowX: 'auto' }}>
          <table style={{ borderCollapse: 'collapse', width: '100%', minWidth: 640 }}>
            <thead>
              <tr>
                <th scope="col" style={th}>User</th>
                <th scope="col" style={th}>Role</th>
                <th scope="col" style={th}>Since</th>
                <th scope="col" style={th}>Expires</th>
                <th scope="col" style={{ ...th, textAlign: 'right' }}>Actions</th>
              </tr>
            </thead>
            <tbody>
              {active.map((a) => (
                <tr key={a.id}>
                  <td style={{ ...td }} className="lc-tabular">{a.userId}</td>
                  <td style={td}>{a.role}</td>
                  <td style={{ ...td, whiteSpace: 'nowrap' }} className="lc-tabular">{fmtDate(a.createdAt)}</td>
                  <td style={{ ...td, whiteSpace: 'nowrap' }} className="lc-tabular">
                    {a.expiresAt ? fmtDate(a.expiresAt) : '—'}</td>
                  <td style={{ ...td, textAlign: 'right' }}>
                    <Button variant="danger" loading={busy === `revoke-${a.id}`}
                      onClick={() => void onRevoke(a.id)}>Revoke</Button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

function tenantName(tenants: readonly Tenant[], id: string): string {
  return tenants.find((t) => t.id === id)?.name ?? id;
}

function SupportSection({ grants, tenants, canRequest, canApprove, busy, onRequest, onApprove, onReject }: {
  readonly grants: readonly PlatformSupportGrant[];
  readonly tenants: readonly Tenant[];
  readonly canRequest: boolean;
  readonly canApprove: boolean;
  readonly busy: string | null;
  readonly onRequest: (subjectTenantId: string, reason: string) => Promise<void>;
  readonly onApprove: (id: string) => Promise<void>;
  readonly onReject: (id: string) => Promise<void>;
}): JSX.Element {
  const [subjectTenantId, setSubjectTenantId] = useState('');
  const [reason, setReason] = useState('');

  return (
    <section style={{ display: 'grid', gap: space[3] }}>
      <h2 style={{ fontSize: fontSize.section, fontWeight: 600 }}>
        Support access <span style={{ color: color.fgMuted, fontWeight: 400 }}>({grants.length})</span>
      </h2>
      <p style={{ margin: 0, color: color.fgMuted, fontSize: fontSize.body }}>
        Time-boxed, tenant-scoped access. A support engineer requests; a security admin (a distinct
        party) approves. Approval is stepped up on the server.
      </p>

      {canRequest && (
        <form
          className="lc-card"
          style={{ padding: space[4], display: 'flex', gap: space[3], alignItems: 'end', flexWrap: 'wrap' }}
          onSubmit={(e) => {
            e.preventDefault();
            void onRequest(subjectTenantId, reason).then(() => { setSubjectTenantId(''); setReason(''); });
          }}
        >
          <TenantSelect id="support-tenant" tenants={tenants} value={subjectTenantId} onChange={setSubjectTenantId} />
          <div style={{ flex: '1 1 220px' }}>
            <Field label="Reason" value={reason} onChange={(e) => setReason(e.target.value)}
              placeholder="Ticket / justification" />
          </div>
          <Button type="submit" loading={busy === 'support-request'} disabled={subjectTenantId === ''}>
            Request access
          </Button>
        </form>
      )}

      {grants.length === 0 ? (
        <p style={{ margin: 0, color: color.fgMuted, fontSize: fontSize.body }}>No support-access grants.</p>
      ) : (
        <div className="lc-card" style={{ overflowX: 'auto' }}>
          <table style={{ borderCollapse: 'collapse', width: '100%', minWidth: 720 }}>
            <thead>
              <tr>
                <th scope="col" style={th}>Tenant</th>
                <th scope="col" style={th}>Requester</th>
                <th scope="col" style={th}>Reason</th>
                <th scope="col" style={th}>Status</th>
                <th scope="col" style={th}>Expires</th>
                {canApprove && <th scope="col" style={{ ...th, textAlign: 'right' }}>Actions</th>}
              </tr>
            </thead>
            <tbody>
              {grants.map((g) => (
                <tr key={g.id}>
                  <td style={td}>{tenantName(tenants, g.subjectTenantId)}</td>
                  <td style={td} className="lc-tabular">{g.requesterUserId}</td>
                  <td style={td}>{g.reason}</td>
                  <td style={td}><StatusChip status={g.active ? 'active' : g.status} /></td>
                  <td style={{ ...td, whiteSpace: 'nowrap' }} className="lc-tabular">
                    {g.expiresAt ? fmtDate(g.expiresAt) : '—'}</td>
                  {canApprove && (
                    <td style={{ ...td, textAlign: 'right', whiteSpace: 'nowrap' }}>
                      {g.status === 'pending' ? (
                        <span style={{ display: 'inline-flex', gap: space[2] }}>
                          <Button loading={busy === `support-approve-${g.id}`}
                            onClick={() => void onApprove(g.id)}>Approve</Button>
                          <Button variant="secondary" loading={busy === `support-reject-${g.id}`}
                            onClick={() => void onReject(g.id)}>Reject</Button>
                        </span>
                      ) : (
                        <span style={{ color: color.fgMuted, fontSize: fontSize.meta }}>decided</span>
                      )}
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

function OffboardSection({ requests, tenants, busy, onRequest, onApprove, onReject }: {
  readonly requests: readonly PlatformOffboardRequest[];
  readonly tenants: readonly Tenant[];
  readonly busy: string | null;
  readonly onRequest: (subjectTenantId: string, reason: string) => Promise<void>;
  readonly onApprove: (id: string) => Promise<void>;
  readonly onReject: (id: string) => Promise<void>;
}): JSX.Element {
  const [subjectTenantId, setSubjectTenantId] = useState('');
  const [reason, setReason] = useState('');

  return (
    <section style={{ display: 'grid', gap: space[3] }}>
      <h2 style={{ fontSize: fontSize.section, fontWeight: 600 }}>
        Offboarding <span style={{ color: color.fgMuted, fontWeight: 400 }}>({requests.length})</span>
      </h2>
      <p style={{ margin: 0, color: color.fgMuted, fontSize: fontSize.body }}>
        Tenant termination is irreversible and two-party: the approver must be a distinct operator
        from the requester. The server enforces the separation.
      </p>

      <form
        className="lc-card"
        style={{ padding: space[4], display: 'flex', gap: space[3], alignItems: 'end', flexWrap: 'wrap' }}
        onSubmit={(e) => {
          e.preventDefault();
          void onRequest(subjectTenantId, reason).then(() => { setSubjectTenantId(''); setReason(''); });
        }}
      >
        <TenantSelect id="offboard-tenant" tenants={tenants} value={subjectTenantId} onChange={setSubjectTenantId} />
        <div style={{ flex: '1 1 220px' }}>
          <Field label="Reason" value={reason} onChange={(e) => setReason(e.target.value)}
            placeholder="Contract ended / justification" />
        </div>
        <Button type="submit" variant="danger" loading={busy === 'offboard-request'} disabled={subjectTenantId === ''}>
          Request offboard
        </Button>
      </form>

      {requests.length === 0 ? (
        <p style={{ margin: 0, color: color.fgMuted, fontSize: fontSize.body }}>No offboarding requests.</p>
      ) : (
        <div className="lc-card" style={{ overflowX: 'auto' }}>
          <table style={{ borderCollapse: 'collapse', width: '100%', minWidth: 720 }}>
            <thead>
              <tr>
                <th scope="col" style={th}>Tenant</th>
                <th scope="col" style={th}>Requester</th>
                <th scope="col" style={th}>Reason</th>
                <th scope="col" style={th}>Status</th>
                <th scope="col" style={{ ...th, textAlign: 'right' }}>Actions</th>
              </tr>
            </thead>
            <tbody>
              {requests.map((r) => (
                <tr key={r.id}>
                  <td style={td}>{tenantName(tenants, r.subjectTenantId)}</td>
                  <td style={td} className="lc-tabular">{r.requesterUserId}</td>
                  <td style={td}>{r.reason}</td>
                  <td style={td}><StatusChip status={r.status} /></td>
                  <td style={{ ...td, textAlign: 'right', whiteSpace: 'nowrap' }}>
                    {r.status === 'pending' ? (
                      <span style={{ display: 'inline-flex', gap: space[2] }}>
                        <Button variant="danger" loading={busy === `offboard-approve-${r.id}`}
                          onClick={() => void onApprove(r.id)}>Approve</Button>
                        <Button variant="secondary" loading={busy === `offboard-reject-${r.id}`}
                          onClick={() => void onReject(r.id)}>Reject</Button>
                      </span>
                    ) : (
                      <span style={{ color: color.fgMuted, fontSize: fontSize.meta }}>decided</span>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

function SecuritySection({ events }: { readonly events: readonly PlatformSecurityEvent[] }): JSX.Element {
  return (
    <section style={{ display: 'grid', gap: space[3] }}>
      <h2 style={{ fontSize: fontSize.section, fontWeight: 600 }}>
        Security events <span style={{ color: color.fgMuted, fontWeight: 400 }}>({events.length})</span>
      </h2>
      <p style={{ margin: 0, color: color.fgMuted, fontSize: fontSize.body }}>
        Append-only record of platform-level actions. Read-only.
      </p>

      {events.length === 0 ? (
        <p style={{ margin: 0, color: color.fgMuted, fontSize: fontSize.body }}>No security events recorded.</p>
      ) : (
        <div className="lc-card" style={{ overflowX: 'auto' }}>
          <table style={{ borderCollapse: 'collapse', width: '100%', minWidth: 640 }}>
            <thead>
              <tr>
                <th scope="col" style={th}>When</th>
                <th scope="col" style={th}>Kind</th>
                <th scope="col" style={th}>Actor</th>
                <th scope="col" style={th}>Detail</th>
              </tr>
            </thead>
            <tbody>
              {events.map((ev) => (
                <tr key={ev.id}>
                  <td style={{ ...td, whiteSpace: 'nowrap' }} className="lc-tabular">{fmtDate(ev.at)}</td>
                  <td style={td}>{ev.kind}</td>
                  <td style={td} className="lc-tabular">{ev.actorUserId}</td>
                  <td style={td}>{ev.detail}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
