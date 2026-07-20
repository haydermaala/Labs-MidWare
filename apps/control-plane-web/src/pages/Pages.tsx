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
import { OnboardDrawer } from './OnboardDrawer';

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
        <div key={i} className="lc-skeleton" style={{ height: 44 }} />
      ))}
    </div>
  );
}

/** A composed empty state: icon, title, guidance, and an optional action. */
export function EmptyState({ icon, title, children, action }: {
  readonly icon: React.ReactNode;
  readonly title: string;
  readonly children: React.ReactNode;
  readonly action?: React.ReactNode;
}): JSX.Element {
  return (
    <div className="lc-empty">
      <span className="lc-empty__icon" aria-hidden="true">{icon}</span>
      <span className="lc-empty__title">{title}</span>
      <span className="lc-empty__body">{children}</span>
      {action}
    </div>
  );
}

/** Inline Lucide icon (24×24 stroke) for empty states and accents. */
export function Glyph({ path, size = 22 }: { readonly path: string; readonly size?: number }): JSX.Element {
  return (
    <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor"
      strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      {path.split('|').map((d, i) => <path key={i} d={d} />)}
    </svg>
  );
}

/** A few shared Lucide path sets. */
export const glyphs = {
  server: 'M5 3h14a2 2 0 0 1 2 2v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z|M5 13h14a2 2 0 0 1 2 2v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4a2 2 0 0 1 2-2z|M6 7h.01|M6 17h.01',
  users: 'M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2|M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8z|M22 21v-2a4 4 0 0 0-3-3.87|M16 3.13a4 4 0 0 1 0 7.75',
  inbox: 'M22 12h-6l-2 3h-4l-2-3H2|M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z',
  activity: 'M22 12h-4l-3 9L9 3l-3 9H2',
} as const;

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
    <div className="lc-stat">
      <span className="lc-stat__label">{label}</span>
      <span className="lc-stat__value" style={{ color: valueColor }}>{value}</span>
    </div>
  );
}

export function FleetPage(): JSX.Element {
  const { token, activeTenantId, activeRole } = useAuth();
  const load = useCallback((t: string, id: string) => listGateways(fleetOptions(t), id), []);
  const { state, data, reload } = useTenantData(load);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [onboarding, setOnboarding] = useState(false);
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
      <div style={{ display: 'flex', alignItems: 'start', gap: space[4], flexWrap: 'wrap' }}>
        <div style={{ flex: 1, minWidth: 0 }}>
          <PageHeader title="Fleet" description="Gateways enrolled in this laboratory, with live connectivity status." />
        </div>
        {canManage && state !== 'no-tenant' && (
          <Button onClick={() => setOnboarding(true)}>Add gateway</Button>
        )}
      </div>
      <OnboardDrawer open={onboarding} onClose={() => setOnboarding(false)} onEnrolled={reload} />
      {state === 'no-tenant' ? <Notice tone="muted">Select a laboratory to view its fleet.</Notice>
        : state === 'loading' ? <Skeleton />
          : state === 'denied' ? <Notice tone="error">You do not have permission to view this fleet.</Notice>
            : state === 'error' ? <Notice tone="error">Could not load gateways. Try again shortly.</Notice>
              : (data ?? []).length === 0
                ? (
                  <EmptyState
                    icon={<Glyph path={glyphs.server} />}
                    title="No gateways enrolled yet"
                    action={canManage ? <Button onClick={() => setOnboarding(true)}>Add gateway</Button> : undefined}
                  >
                    {canManage
                      ? 'Add a gateway to connect an on-site analyzer. It captures output locally and reports here over a secure outbound channel.'
                      : 'An administrator can add the first gateway to connect an analyzer.'}
                  </EmptyState>
                )
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
            <th scope="col" style={th}>Messages</th>
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
              <td style={{ ...td, whiteSpace: 'nowrap' }} className="lc-tabular" title="captured · delivered · pending · dead">
                {g.telemetry.captured === 0 && g.telemetry.delivered === 0 && g.telemetry.pending === 0
                  ? <span style={{ color: color.fgMuted }}>—</span>
                  : (
                    <span style={{ fontSize: fontSize.meta }}>
                      <strong>{g.telemetry.captured}</strong> captured
                      <span style={{ color: color.fgMuted }}> · {g.telemetry.delivered} delivered</span>
                      {g.telemetry.pending > 0 && <span style={{ color: color.fgMuted }}> · {g.telemetry.pending} pending</span>}
                      {g.telemetry.dead > 0 && <span style={{ color: color.danger }}> · {g.telemetry.dead} dead</span>}
                    </span>
                  )}
              </td>
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
