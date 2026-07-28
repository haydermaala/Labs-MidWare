// Platform (super-admin) console (P6). Gated by the caller's PLATFORM roles, disjoint
// from tenant membership. Surfaces: the tenant registry/lifecycle, time-boxed support
// access (request → distinct-party approve), two-party tenant offboarding, an append-only
// security-event log, and platform-role administration. Each section is shown only to the
// roles that can use it. The server is the authority on every gate; the UI mirrors the
// reasons and prompts for step-up (MFA / fresh re-auth) when the server requires it.

import { useCallback, useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import {
  approveOffboard, approveSupportGrant, archiveTenant, cancelTenantOffboarding, exportTenant, grantPlatformRole,
  listOffboardRequests, listPlatformRoleAssignments, listPlatformTenants, listSecurityEvents,
  listSupportGrants, platformOverview, provisionTenant, reactivatePlatformTenant, rejectOffboard,
  rejectSupportGrant, requestOffboard, requestSupportGrant, revokePlatformRole, setTenantLegalHold,
  setTenantSubscription, suspendTenant,
  type ControlPlaneOptions, type PlatformOffboardRequest, type PlatformOverview,
  type PlatformRoleAssignment, type PlatformSecurityEvent, type PlatformSupportGrant, type Tenant,
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

// Billing plan ids (Billing.cs Plans.*). Trial is the no-subscription default; the
// paid plans unlock features (e.g. custom roles) per the entitlement matrix.
const PLANS = ['trial', 'pilot', 'laboratory', 'network'] as const;

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
  const { token, user } = useAuth();
  const { guard } = useStepUp();
  const platform = usePlatformRoles();
  const isRoot = platform.has('platform-root-owner');
  const canManageRoles = isRoot;
  const canManageTenants = isRoot || platform.has('platform-operations-admin');
  const canApproveSupport = isRoot || platform.has('platform-security-admin');
  const canRequestSupport = isRoot || platform.has('platform-support-engineer');
  const canOffboard = isRoot || platform.has('platform-operations-admin');
  const canReadSecurity = isRoot || platform.has('platform-security-admin') || platform.has('platform-auditor');
  const canManageSubscription = isRoot || platform.has('platform-billing-admin');

  const [overview, setOverview] = useState<PlatformOverview | null>(null);
  const [needsStepUp, setNeedsStepUp] = useState(false);
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
    // empty section rather than failing the whole page. A step-up denial is DIFFERENT
    // — the data exists but assurance is missing — so it must never render as a silent
    // empty state (an operator would read "0 tenants" as "there are no tenants").
    let stepUp = false;
    const read = async <T,>(p: Promise<T>, fallback: T): Promise<T> => {
      try {
        return await p;
      } catch (e) {
        if ((e as { requiresStepUp?: boolean }).requiresStepUp === true) {
          stepUp = true;
        }
        return fallback;
      }
    };
    const [ov, a, t, s, ob, ev] = await Promise.all([
      read(platformOverview(o), null as PlatformOverview | null),
      canManageRoles ? read(listPlatformRoleAssignments(o), [] as readonly PlatformRoleAssignment[]) : Promise.resolve([]),
      read(listPlatformTenants(o), [] as readonly Tenant[]),
      canApproveSupport ? read(listSupportGrants(o), [] as readonly PlatformSupportGrant[]) : Promise.resolve([]),
      canOffboard ? read(listOffboardRequests(o), [] as readonly PlatformOffboardRequest[]) : Promise.resolve([]),
      canReadSecurity ? read(listSecurityEvents(o, 50), [] as readonly PlatformSecurityEvent[]) : Promise.resolve([]),
    ]);
    setNeedsStepUp(stepUp);
    setOverview(ov);
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

      {needsStepUp && (
        <StepUpRequiredBanner
          mfaEnrolled={user?.mfaEnabled === true}
          busy={busy === 'verify'}
          onVerify={() => run('verify', async () => { await platformOverview(opts(token!)); })}
        />
      )}

      <div style={{ display: 'grid', gap: space[5] }}>
        {overview !== null && <OverviewSection overview={overview} />}

        <TenantsSection
          tenants={tenants}
          canManage={canManageTenants}
          canOffboard={canOffboard}
          busy={busy}
          onProvision={(name) => run('provision', async () => { await provisionTenant(opts(token!), name); })}
          onSuspend={(id) => run(`suspend-${id}`, () => suspendTenant(opts(token!), id))}
          onReactivate={(id) => run(`react-${id}`, () => reactivatePlatformTenant(opts(token!), id))}
          onArchive={(id) => run(`archive-${id}`, () => archiveTenant(opts(token!), id))}
          onCancelOffboard={(id) => run(`cancel-off-${id}`, () => cancelTenantOffboarding(opts(token!), id))}
          onSetLegalHold={(id, hold) => run(`hold-${id}`, () => setTenantLegalHold(opts(token!), id, hold))}
          onExport={(id) => run(`export-${id}`, async () => {
            const data = await exportTenant(opts(token!), id);
            // Trigger a client-side download of the artifact.
            const blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' });
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = `tenant-${id}-export.json`;
            document.body.appendChild(a);
            a.click();
            a.remove();
            URL.revokeObjectURL(url);
          })}
        />

        {canManageSubscription && (
          <SubscriptionSection
            tenants={tenants}
            busy={busy}
            onSetPlan={(tenantId, planId) => run('subscription', async () => {
              await setTenantSubscription(opts(token!), tenantId, planId);
            })}
          />
        )}

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
    status === 'approved' || status === 'active' || status === 'trial' ? color.ok
    : status === 'rejected' || status === 'expired' || status === 'archived' || status === 'offboarding' ? color.danger
    : color.warn; // pending / suspended / grace / provisioning
  return (
    <span style={{
      display: 'inline-block', padding: `2px ${space[2]}px`, borderRadius: 999,
      fontSize: fontSize.meta, fontWeight: 600, color: tone,
      border: `1px solid ${tone}`, background: 'transparent', whiteSpace: 'nowrap',
    }}>{status}</span>
  );
}

/** The lifecycle status shown for a tenant: the authoritative P7 status, falling back
 *  to the legacy booleans for an older server. Lower-cased for display/color lookup. */
function statusOf(t: Tenant): string {
  return (t.status ?? (t.offboarded ? 'archived' : t.active ? 'active' : 'suspended')).toLowerCase();
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

/**
 * Shown when a platform read was denied for missing assurance rather than missing data.
 * Two genuinely different cases, and conflating them strands the operator:
 *  - MFA enrolled → the session just needs a step-up; verifying resolves it.
 *  - MFA NOT enrolled → step-up can never satisfy an MFA gate (the server only marks a
 *    session MFA-satisfied for enrolled users), so the only way forward is to enrol.
 * Break-glass roles (Root Owner) require MFA for EVERY permission, including reads.
 */
function StepUpRequiredBanner({ mfaEnrolled, busy, onVerify }: {
  readonly mfaEnrolled: boolean;
  readonly busy: boolean;
  readonly onVerify: () => void;
}): JSX.Element {
  return (
    <div role="alert" style={{
      margin: `0 0 ${space[4]}px`, padding: space[4], borderRadius: 6,
      border: `1px solid ${color.warn}`, background: 'color-mix(in oklch, var(--lc-warn) 8%, transparent)',
      display: 'flex', gap: space[3], alignItems: 'center', flexWrap: 'wrap',
    }}>
      <div style={{ flex: '1 1 320px', fontSize: fontSize.body }}>
        <strong>Additional verification required.</strong>{' '}
        {mfaEnrolled
          ? 'Some platform data is hidden until you re-verify your identity. Sections may appear empty until then.'
          : 'Your platform role requires multi-factor authentication, which is not enrolled on this account. '
            + 'Platform data stays hidden — including counts, which will read as zero — until you enrol MFA from the Security page.'}
      </div>
      {mfaEnrolled
        ? <Button loading={busy} onClick={onVerify}>Verify identity</Button>
        : <Link to="/security" className="lc-btn lc-btn--primary">Enrol MFA</Link>}
    </div>
  );
}

/** A labelled count "pill" — a status chip or plan name with its tally. */
function CountPill({ label, n, chip }: { readonly label: string; readonly n: number; readonly chip?: boolean }): JSX.Element {
  return (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: space[2] }}>
      {chip ? <StatusChip status={label} /> : <span style={{ fontSize: fontSize.meta, fontWeight: 600 }}>{label}</span>}
      <span style={{ fontSize: fontSize.meta, color: color.fgMuted }} className="lc-tabular">{n}</span>
    </span>
  );
}

/** §13.1 overview: total tenants, payment health, and counts by lifecycle state + plan. */
function OverviewSection({ overview }: { readonly overview: PlatformOverview }): JSX.Element {
  const byStatus = Object.entries(overview.tenantsByStatus).sort((a, b) => a[0].localeCompare(b[0]));
  const byPlan = Object.entries(overview.tenantsByPlan).sort((a, b) => a[0].localeCompare(b[0]));
  return (
    <section style={{ display: 'grid', gap: space[3] }}>
      <h2 style={{ fontSize: fontSize.section, fontWeight: 600 }}>Overview</h2>
      <div style={{ display: 'flex', gap: space[3], flexWrap: 'wrap' }}>
        <div className="lc-card" style={{ padding: space[4], minWidth: 140 }}>
          <div style={{ fontSize: fontSize.meta, color: color.fgMuted }}>Tenants</div>
          <div style={{ fontSize: fontSize.title, fontWeight: 700 }} className="lc-tabular">{overview.totalTenants}</div>
        </div>
        <div className="lc-card" style={{ padding: space[4], minWidth: 140 }}>
          <div style={{ fontSize: fontSize.meta, color: color.fgMuted }}>Past due</div>
          <div style={{ fontSize: fontSize.title, fontWeight: 700,
            color: overview.pastDueCount > 0 ? color.danger : color.fg }} className="lc-tabular">
            {overview.pastDueCount}</div>
        </div>
        <div className="lc-card" style={{ padding: space[4], flex: '1 1 200px', display: 'grid', gap: space[2] }}>
          <div style={{ fontSize: fontSize.meta, color: color.fgMuted }}>By lifecycle state</div>
          <div style={{ display: 'flex', gap: space[3], flexWrap: 'wrap' }}>
            {byStatus.length === 0 ? <span style={{ color: color.fgMuted, fontSize: fontSize.meta }}>—</span>
              : byStatus.map(([s, n]) => <CountPill key={s} label={s.toLowerCase()} n={n} chip />)}
          </div>
        </div>
        <div className="lc-card" style={{ padding: space[4], flex: '1 1 200px', display: 'grid', gap: space[2] }}>
          <div style={{ fontSize: fontSize.meta, color: color.fgMuted }}>By plan</div>
          <div style={{ display: 'flex', gap: space[3], flexWrap: 'wrap' }}>
            {byPlan.length === 0 ? <span style={{ color: color.fgMuted, fontSize: fontSize.meta }}>—</span>
              : byPlan.map(([p, n]) => <CountPill key={p} label={p} n={n} />)}
          </div>
        </div>
      </div>
    </section>
  );
}

function TenantsSection({ tenants, canManage, canOffboard, busy, onProvision, onSuspend, onReactivate, onArchive, onCancelOffboard, onSetLegalHold, onExport }: {
  readonly tenants: readonly Tenant[];
  readonly canManage: boolean;
  readonly canOffboard: boolean;
  readonly busy: string | null;
  readonly onProvision: (name: string) => Promise<void>;
  readonly onSuspend: (id: string) => Promise<void>;
  readonly onReactivate: (id: string) => Promise<void>;
  readonly onArchive: (id: string) => Promise<void>;
  readonly onCancelOffboard: (id: string) => Promise<void>;
  readonly onSetLegalHold: (id: string, hold: boolean) => Promise<void>;
  readonly onExport: (id: string) => Promise<void>;
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
              const status = statusOf(t);
              return (
                <tr key={t.id}>
                  <td style={td}><div style={{ fontWeight: 600 }}>{t.name}</div>
                    <div style={{ fontSize: 11, color: color.fgMuted }} className="lc-tabular">{t.id}</div></td>
                  <td style={td}><StatusChip status={status} /></td>
                  <td style={{ ...td, whiteSpace: 'nowrap' }} className="lc-tabular">{fmtDate(t.createdAt)}</td>
                  {canManage && (
                    <td style={{ ...td, textAlign: 'right' }}>
                      <span style={{ display: 'inline-flex', gap: space[2], alignItems: 'center', justifyContent: 'flex-end', flexWrap: 'wrap' }}>
                      <Button variant="secondary" loading={busy === `export-${t.id}`}
                        onClick={() => void onExport(t.id)}>Export</Button>
                      {status === 'archived' ? (
                        <span style={{ color: color.fgMuted, fontSize: fontSize.meta }}>terminal</span>
                      ) : status === 'offboarding' ? (
                        // Mid-pipeline: a distinct-party approval already began offboarding;
                        // complete it (archive) or cancel during cooling-off. Archiving is
                        // blocked until cooling-off elapses and while a legal hold is set —
                        // enforced server-side; mirrored here to explain the disabled state.
                        canOffboard ? (
                          <OffboardingActions
                            tenant={t}
                            busy={busy}
                            onCancel={() => void onCancelOffboard(t.id)}
                            onArchive={() => void onArchive(t.id)}
                            onToggleHold={(hold) => void onSetLegalHold(t.id, hold)}
                          />
                        ) : (
                          <span style={{ color: color.fgMuted, fontSize: fontSize.meta }}>offboarding</span>
                        )
                      ) : t.active ? (
                        <Button variant="secondary" loading={busy === `suspend-${t.id}`}
                          onClick={() => void onSuspend(t.id)}>Suspend</Button>
                      ) : (
                        <Button variant="secondary" loading={busy === `react-${t.id}`}
                          onClick={() => void onReactivate(t.id)}>Reactivate</Button>
                      )}
                      </span>
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

/** Actions for a tenant mid-offboarding: cancel, complete (archive), and the legal-hold
 *  toggle. Archive is disabled — with an explanation — while a hold is set or cooling-off
 *  has not elapsed, mirroring the server's guards. */
function OffboardingActions({ tenant, busy, onCancel, onArchive, onToggleHold }: {
  readonly tenant: Tenant;
  readonly busy: string | null;
  readonly onCancel: () => void;
  readonly onArchive: () => void;
  readonly onToggleHold: (hold: boolean) => void;
}): JSX.Element {
  const held = tenant.legalHold === true;
  const coolingUntil = tenant.coolingOffUntil ? new Date(tenant.coolingOffUntil) : null;
  const coolingActive = coolingUntil !== null && coolingUntil.getTime() > Date.now();
  const archiveBlocked = held || coolingActive;
  const reason = held ? 'Legal hold in place'
    : coolingActive ? `Cooling-off until ${coolingUntil!.toISOString().slice(0, 10)}`
    : '';

  return (
    <span style={{ display: 'inline-flex', gap: space[2], alignItems: 'center', justifyContent: 'flex-end', flexWrap: 'wrap' }}>
      {reason !== '' && <span style={{ fontSize: fontSize.meta, color: color.fgMuted }}>{reason}</span>}
      <Button variant="secondary" loading={busy === `hold-${tenant.id}`}
        onClick={() => onToggleHold(!held)}>{held ? 'Lift hold' : 'Hold'}</Button>
      <Button variant="secondary" loading={busy === `cancel-off-${tenant.id}`}
        onClick={onCancel}>Cancel</Button>
      <Button variant="danger" loading={busy === `archive-${tenant.id}`}
        disabled={archiveBlocked} title={reason}
        onClick={onArchive}>Archive</Button>
    </span>
  );
}

function SubscriptionSection({ tenants, busy, onSetPlan }: {
  readonly tenants: readonly Tenant[];
  readonly busy: string | null;
  readonly onSetPlan: (tenantId: string, planId: string) => Promise<void>;
}): JSX.Element {
  const [tenantId, setTenantId] = useState('');
  const [planId, setPlanId] = useState<string>('pilot');

  return (
    <section style={{ display: 'grid', gap: space[3] }}>
      <h2 style={{ fontSize: fontSize.section, fontWeight: 600 }}>Subscription</h2>
      <p style={{ margin: 0, color: color.fgMuted, fontSize: fontSize.body }}>
        Set a tenant&rsquo;s plan. Paid plans unlock entitlements (e.g. custom roles); this writes the
        subscription directly, outside the checkout flow.
      </p>

      <form
        className="lc-card"
        style={{ padding: space[4], display: 'flex', gap: space[3], alignItems: 'end', flexWrap: 'wrap' }}
        onSubmit={(e) => { e.preventDefault(); void onSetPlan(tenantId, planId); }}
      >
        <TenantSelect id="subscription-tenant" tenants={tenants} value={tenantId} onChange={setTenantId} />
        <div className="lc-field" style={{ flex: '0 1 200px' }}>
          <label className="lc-field__label" htmlFor="subscription-plan">Plan</label>
          <select id="subscription-plan" className="lc-input" value={planId} onChange={(e) => setPlanId(e.target.value)}>
            {PLANS.map((p) => <option key={p} value={p}>{p}</option>)}
          </select>
        </div>
        <Button type="submit" loading={busy === 'subscription'} disabled={tenantId === ''}>Apply plan</Button>
      </form>
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
