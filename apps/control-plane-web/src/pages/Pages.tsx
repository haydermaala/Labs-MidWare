// Authenticated pages: Dashboard, Fleet, Audit, Security.
// Every data view carries the full state matrix (loading / empty / error /
// permission-denied / loaded). No PHI or result values cross this surface.

import { useCallback, useEffect, useState } from 'react';
import {
  listGateways, getAudit, decommissionGateway,
  type AuditEvent, type ControlPlaneOptions, type GatewaySummary,
} from '@lab-connect/api-client';
import { Button, StatusBadge, color, fontSize, space } from '@lab-connect/ui';
import type { StatusKind } from '@lab-connect/ui';
import { API_BASE } from '../config';
import { useAuth } from '../auth/AuthProvider';

/** The fleet endpoints accept any bearer credential; the console sends the
 * operator's session token and the server authorizes by membership role. */
function fleetOptions(token: string): ControlPlaneOptions {
  return { baseUrl: API_BASE, adminToken: token };
}

function fmt(instant: string | null): string {
  if (instant === null) return '—';
  const d = new Date(instant);
  return Number.isNaN(d.getTime()) ? instant : d.toISOString().replace('T', ' ').slice(0, 16) + 'Z';
}

export function PageHeader({ title, description }: { readonly title: string; readonly description: string }): JSX.Element {
  return (
    <header style={{ display: 'grid', gap: 4, marginBottom: space[5] }}>
      <h1 style={{ fontSize: fontSize.title, fontWeight: 600 }}>{title}</h1>
      <p style={{ margin: 0, color: color.fgMuted, fontSize: fontSize.body }}>{description}</p>
    </header>
  );
}

function Notice({ tone, children }: { readonly tone: 'muted' | 'error'; readonly children: React.ReactNode }): JSX.Element {
  return (
    <p
      role={tone === 'error' ? 'alert' : undefined}
      style={{
        margin: 0, padding: space[4], borderRadius: 6,
        border: `1px solid ${tone === 'error' ? color.danger : color.border}`,
        color: tone === 'error' ? color.danger : color.fgMuted,
        background: color.surface1, fontSize: fontSize.body,
      }}
    >
      {children}
    </p>
  );
}

function Skeleton({ rows = 3 }: { readonly rows?: number }): JSX.Element {
  return (
    <div aria-hidden="true" style={{ display: 'grid', gap: space[2] }}>
      {Array.from({ length: rows }, (_, i) => (
        <div key={i} style={{ height: 36, borderRadius: 4, background: color.surface2 }} />
      ))}
    </div>
  );
}

/** Shared loader for tenant-scoped data with the full state matrix. */
function useTenantData<T>(load: (token: string, tenantId: string) => Promise<T>): {
  state: 'loading' | 'ready' | 'error' | 'denied' | 'no-tenant';
  data: T | null;
  reload: () => void;
} {
  const { token, activeTenantId } = useAuth();
  const [state, setState] = useState<'loading' | 'ready' | 'error' | 'denied' | 'no-tenant'>('loading');
  const [data, setData] = useState<T | null>(null);
  const [nonce, setNonce] = useState(0);

  useEffect(() => {
    if (token === null || activeTenantId === null) {
      setState('no-tenant');
      return;
    }
    let cancelled = false;
    setState('loading');
    load(token, activeTenantId)
      .then((result) => { if (!cancelled) { setData(result); setState('ready'); } })
      .catch((e: unknown) => {
        if (cancelled) return;
        const status = (e as { status?: number }).status;
        setState(status === 401 || status === 403 ? 'denied' : 'error');
      });
    return () => { cancelled = true; };
  }, [token, activeTenantId, nonce, load]);

  return { state, data, reload: () => setNonce((n) => n + 1) };
}

export function DashboardPage(): JSX.Element {
  const { user, memberships, activeTenantId, activeRole } = useAuth();
  const load = useCallback((t: string, id: string) => listGateways(fleetOptions(t), id), []);
  const { state, data } = useTenantData(load);
  const gateways = data ?? [];
  const online = gateways.filter((g) => g.status === 'online').length;
  const offline = gateways.filter((g) => g.status === 'offline').length;
  const active = memberships.find((m) => m.tenantId === activeTenantId);

  return (
    <>
      <PageHeader
        title={active?.tenantName ?? 'Dashboard'}
        description={`Signed in as ${user?.email ?? ''}${activeRole !== null ? ` · ${activeRole}` : ''}`}
      />
      {state === 'no-tenant' ? (
        <Notice tone="muted">
          Your account is not a member of any laboratory yet. Ask an administrator to invite you.
        </Notice>
      ) : state === 'loading' ? <Skeleton rows={2} />
        : state === 'denied' ? <Notice tone="error">You do not have permission to view this laboratory.</Notice>
          : state === 'error' ? <Notice tone="error">Could not load fleet status. Try again shortly.</Notice>
            : (
              <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(180px, 1fr))', gap: space[4] }}>
                <Stat label="Gateways" value={gateways.length} />
                <Stat label="Online" value={online} tone="ok" />
                <Stat label="Offline" value={offline} tone={offline > 0 ? 'warn' : 'muted'} />
                <Stat label="Never seen" value={gateways.filter((g) => g.status === 'never').length} tone="muted" />
              </div>
            )}
      {user?.emailVerified === false && (
        <div style={{ marginTop: space[5] }}>
          <Notice tone="muted">
            Your email address is not verified yet. Verify it from the Security page to receive
            operational alerts.
          </Notice>
        </div>
      )}
    </>
  );
}

function Stat({ label, value, tone = 'muted' }: {
  readonly label: string; readonly value: number; readonly tone?: 'ok' | 'warn' | 'muted';
}): JSX.Element {
  const valueColor = tone === 'ok' ? color.ok : tone === 'warn' ? color.warn : color.fg;
  return (
    <div className="lc-card" style={{ padding: space[4], display: 'grid', gap: space[1] }}>
      <span style={{ fontSize: fontSize.meta, color: color.fgMuted, fontWeight: 500 }}>{label}</span>
      <span className="lc-tabular" style={{ fontSize: 28, fontWeight: 600, color: valueColor }}>{value}</span>
    </div>
  );
}

export function FleetPage(): JSX.Element {
  const { token, activeTenantId, activeRole } = useAuth();
  const load = useCallback((t: string, id: string) => listGateways(fleetOptions(t), id), []);
  const { state, data, reload } = useTenantData(load);
  const [busyId, setBusyId] = useState<string | null>(null);
  const canManage = activeRole === 'owner' || activeRole === 'tenant-admin' || activeRole === 'lab-admin';

  async function decommission(gatewayId: string): Promise<void> {
    if (token === null || activeTenantId === null) return;
    setBusyId(gatewayId);
    try {
      await decommissionGateway(fleetOptions(token), activeTenantId, gatewayId);
      reload();
    } finally {
      setBusyId(null);
    }
  }

  return (
    <>
      <PageHeader title="Fleet" description="Gateways enrolled in this laboratory, with live connectivity status." />
      {state === 'no-tenant' ? <Notice tone="muted">Select a laboratory to view its fleet.</Notice>
        : state === 'loading' ? <Skeleton />
          : state === 'denied' ? <Notice tone="error">You do not have permission to view this fleet.</Notice>
            : state === 'error' ? <Notice tone="error">Could not load gateways. Try again shortly.</Notice>
              : (data ?? []).length === 0
                ? <Notice tone="muted">No gateways enrolled yet. An administrator can issue an enrollment token to add the first one.</Notice>
                : <GatewayTable gateways={data ?? []} canManage={canManage} busyId={busyId} onDecommission={decommission} />}
    </>
  );
}

const th: React.CSSProperties = {
  textAlign: 'left', padding: `${space[2]}px ${space[3]}px`, fontSize: fontSize.meta,
  fontWeight: 600, color: color.fgMuted, borderBottom: `1px solid ${color.border}`, whiteSpace: 'nowrap',
};
const td: React.CSSProperties = {
  padding: `${space[2]}px ${space[3]}px`, fontSize: fontSize.table,
  borderBottom: `1px solid ${color.border}`, verticalAlign: 'middle',
};

export function GatewayTable({ gateways, canManage, busyId, onDecommission }: {
  readonly gateways: readonly GatewaySummary[];
  readonly canManage: boolean;
  readonly busyId: string | null;
  readonly onDecommission: (id: string) => void;
}): JSX.Element {
  return (
    <div className="lc-card" style={{ overflowX: 'auto' }}>
      <table style={{ borderCollapse: 'collapse', width: '100%', minWidth: 640 }}>
        <caption className="lc-sr-only">Gateways in this laboratory</caption>
        <thead>
          <tr>
            <th scope="col" style={th}>Gateway</th>
            <th scope="col" style={th}>Status</th>
            <th scope="col" style={th}>Last seen</th>
            <th scope="col" style={th}>Enrolled</th>
            {canManage && <th scope="col" style={{ ...th, textAlign: 'right' }}>Actions</th>}
          </tr>
        </thead>
        <tbody>
          {gateways.map((g) => (
            <tr key={g.id}>
              <td style={td}>
                <div style={{ fontWeight: 600 }}>{g.name}</div>
                <div className="lc-mono" style={{ fontSize: 11, color: color.fgMuted }}>{g.id.slice(0, 16)}…</div>
              </td>
              <td style={td}><StatusBadge status={g.status as StatusKind} /></td>
              <td style={{ ...td, whiteSpace: 'nowrap' }} className="lc-tabular">{fmt(g.lastSeenAt)}</td>
              <td style={{ ...td, whiteSpace: 'nowrap' }} className="lc-tabular">{fmt(g.enrolledAt)}</td>
              {canManage && (
                <td style={{ ...td, textAlign: 'right' }}>
                  <Button
                    variant="danger"
                    disabled={!g.active}
                    loading={busyId === g.id}
                    onClick={() => onDecommission(g.id)}
                  >
                    Decommission
                  </Button>
                </td>
              )}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

export function AuditPage(): JSX.Element {
  const load = useCallback((t: string, id: string) => getAudit(fleetOptions(t), id), []);
  const { state, data } = useTenantData(load);
  const events: readonly AuditEvent[] = data ?? [];

  return (
    <>
      <PageHeader title="Audit" description="Append-only record of administrative and fleet activity for this laboratory." />
      {state === 'no-tenant' ? <Notice tone="muted">Select a laboratory to view its audit trail.</Notice>
        : state === 'loading' ? <Skeleton rows={5} />
          : state === 'denied' ? <Notice tone="error">You do not have permission to view this audit trail.</Notice>
            : state === 'error' ? <Notice tone="error">Could not load audit events. Try again shortly.</Notice>
              : events.length === 0 ? <Notice tone="muted">No audit events recorded yet.</Notice>
                : (
                  <div className="lc-card" style={{ overflowX: 'auto' }}>
                    <table style={{ borderCollapse: 'collapse', width: '100%', minWidth: 560 }}>
                      <thead>
                        <tr>
                          <th scope="col" style={th}>When</th>
                          <th scope="col" style={th}>Event</th>
                          <th scope="col" style={th}>Detail</th>
                        </tr>
                      </thead>
                      <tbody>
                        {[...events].reverse().map((e, i) => (
                          <tr key={`${e.at}-${i}`}>
                            <td style={{ ...td, whiteSpace: 'nowrap', color: color.fgMuted }} className="lc-tabular">{fmt(e.at)}</td>
                            <td style={td}><code style={{ fontSize: 12 }}>{e.kind}</code></td>
                            <td style={{ ...td, color: color.fgMuted, wordBreak: 'break-word' }}>{e.detail}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}
    </>
  );
}

export function SecurityPage(): JSX.Element {
  const { user, token, refresh } = useAuth();
  const [sent, setSent] = useState(false);
  const [busy, setBusy] = useState(false);

  async function sendVerification(): Promise<void> {
    if (token === null) return;
    setBusy(true);
    try {
      const { sendVerification: send } = await import('@lab-connect/api-client');
      await send({ baseUrl: API_BASE, sessionToken: token });
      setSent(true);
    } catch {
      setSent(true); // response is deliberately uniform
    } finally {
      setBusy(false);
      await refresh();
    }
  }

  return (
    <>
      <PageHeader title="Security" description="Account protection for your LabConnect sign-in." />
      <div style={{ display: 'grid', gap: space[4], maxWidth: 640 }}>
        <section className="lc-card" style={{ padding: space[4], display: 'grid', gap: space[3] }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: space[3] }}>
            <h2 style={{ fontSize: fontSize.section, fontWeight: 600 }}>Email address</h2>
            <StatusBadge status={user?.emailVerified === true ? 'active' : 'pending'} />
          </div>
          <p style={{ margin: 0, color: color.fgMuted, fontSize: fontSize.body }}>
            {user?.email} — {user?.emailVerified === true
              ? 'verified.'
              : 'not verified. Verification is required before you can receive operational alerts.'}
          </p>
          {user?.emailVerified !== true && (
            <div>
              <Button onClick={() => void sendVerification()} loading={busy} disabled={sent}>
                {sent ? 'Verification sent' : 'Send verification email'}
              </Button>
            </div>
          )}
        </section>

        <section className="lc-card" style={{ padding: space[4], display: 'grid', gap: space[3] }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: space[3] }}>
            <h2 style={{ fontSize: fontSize.section, fontWeight: 600 }}>Two-factor authentication</h2>
            <StatusBadge status={user?.mfaEnabled === true ? 'active' : 'inactive'} />
          </div>
          <p style={{ margin: 0, color: color.fgMuted, fontSize: fontSize.body }}>
            {user?.mfaEnabled === true
              ? 'Your account requires an authenticator code at sign-in. Recovery codes were issued when you enabled it.'
              : 'Add an authenticator app to require a second factor at sign-in. Strongly recommended for administrators.'}
          </p>
          <p style={{ margin: 0, color: color.fgMuted, fontSize: fontSize.meta }}>
            Enrollment is available from the API in this release; the guided setup screen ships with
            the settings inventory.
          </p>
        </section>
      </div>
    </>
  );
}
