import { useCallback, useState } from 'react';
import {
  listTenants,
  listGateways,
  getAudit,
  deactivateTenant,
  reactivateTenant,
  decommissionGateway,
  type AuditEvent,
  type ControlPlaneOptions,
  type GatewaySummary,
  type Tenant,
} from '@lab-connect/api-client';
import { CONTRACT_VERSION } from '@lab-connect/contracts';
import { tokens } from '@lab-connect/ui';
import { ConnectionForm } from './components/ConnectionForm';
import { TenantList } from './components/TenantList';
import { GatewayList } from './components/GatewayList';
import { AuditPanel } from './components/AuditPanel';

type TenantView = 'gateways' | 'audit';

/**
 * Control-plane operator console. Fleet view: tenants and their gateways with
 * derived liveness (online/offline/last-seen), plus soft lifecycle actions
 * (deactivate/reactivate a tenant, decommission a gateway). Admin-token auth is
 * held in memory only. No PHI or result values cross this surface.
 */
export function App(): JSX.Element {
  const [baseUrl, setBaseUrl] = useState('');
  const [adminToken, setAdminToken] = useState('');
  const [tenants, setTenants] = useState<readonly Tenant[]>([]);
  const [selectedTenantId, setSelectedTenantId] = useState<string | null>(null);
  const [gateways, setGateways] = useState<readonly GatewaySummary[]>([]);
  const [audit, setAudit] = useState<readonly AuditEvent[]>([]);
  const [tenantView, setTenantView] = useState<TenantView>('gateways');
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [busy, setBusy] = useState(false);

  const opts = useCallback(
    (): ControlPlaneOptions => ({ baseUrl, adminToken }),
    [baseUrl, adminToken],
  );

  // Gateways and the audit trail are both tenant-scoped; load them together so the
  // view toggle is instant and lifecycle actions refresh both.
  const loadTenantDetail = useCallback(
    async (tenantId: string): Promise<void> => {
      const [g, a] = await Promise.all([listGateways(opts(), tenantId), getAudit(opts(), tenantId)]);
      setGateways(g);
      setAudit(a);
    },
    [opts],
  );

  const connect = useCallback(async (): Promise<void> => {
    setLoading(true);
    setError(null);
    try {
      const list = await listTenants(opts());
      setTenants(list);
      setSelectedTenantId(null);
      setGateways([]);
      setAudit([]);
    } catch (e) {
      setTenants([]);
      setError(e instanceof Error ? e.message : 'connection failed');
    } finally {
      setLoading(false);
    }
  }, [opts]);

  const selectTenant = useCallback(
    async (tenantId: string): Promise<void> => {
      setSelectedTenantId(tenantId);
      setError(null);
      try {
        await loadTenantDetail(tenantId);
      } catch (e) {
        setGateways([]);
        setAudit([]);
        setError(e instanceof Error ? e.message : 'failed to load tenant detail');
      }
    },
    [loadTenantDetail],
  );

  // Run a lifecycle action, then refresh the affected views.
  const act = useCallback(
    async (fn: () => Promise<void>): Promise<void> => {
      setBusy(true);
      setError(null);
      try {
        await fn();
        setTenants(await listTenants(opts()));
        if (selectedTenantId !== null) {
          await loadTenantDetail(selectedTenantId);
        }
      } catch (e) {
        setError(e instanceof Error ? e.message : 'action failed');
      } finally {
        setBusy(false);
      }
    },
    [opts, selectedTenantId, loadTenantDetail],
  );

  return (
    <main
      style={{
        background: tokens.color.bg,
        color: tokens.color.fg,
        minHeight: '100vh',
        padding: tokens.space[5],
        display: 'grid',
        gap: tokens.space[4],
        fontFamily: 'system-ui, sans-serif',
        alignContent: 'start',
      }}
    >
      <header style={{ display: 'flex', alignItems: 'center', gap: tokens.space[3] }}>
        <h1 style={{ margin: 0, fontSize: 20 }}>lab-connect · control plane</h1>
        <span style={{ color: '#9aa4b2', fontSize: 13 }}>fleet</span>
        <span style={{ marginLeft: 'auto', color: '#9aa4b2', fontSize: 12 }}>
          contract v{CONTRACT_VERSION}
        </span>
      </header>

      <ConnectionForm
        baseUrl={baseUrl}
        adminToken={adminToken}
        loading={loading}
        onBaseUrl={setBaseUrl}
        onAdminToken={setAdminToken}
        onConnect={() => void connect()}
      />

      {error !== null && (
        <p role="alert" style={{ color: tokens.color.danger }}>
          {error}
        </p>
      )}

      {tenants.length > 0 && (
        <section
          style={{
            display: 'grid',
            gridTemplateColumns: 'minmax(280px, 360px) 1fr',
            gap: tokens.space[5],
            alignItems: 'start',
          }}
        >
          <div style={{ display: 'grid', gap: tokens.space[3] }}>
            <h2 style={{ margin: 0, fontSize: 15 }}>Tenants</h2>
            <TenantList
              tenants={tenants}
              selectedId={selectedTenantId}
              busy={busy}
              onSelect={(id) => void selectTenant(id)}
              onDeactivate={(id) => void act(() => deactivateTenant(opts(), id))}
              onReactivate={(id) => void act(() => reactivateTenant(opts(), id))}
            />
          </div>

          <div style={{ display: 'grid', gap: tokens.space[3] }}>
            {selectedTenantId === null ? (
              <>
                <h2 style={{ margin: 0, fontSize: 15 }}>Gateways</h2>
                <p style={{ color: '#9aa4b2' }}>Select a tenant to see its gateways and audit trail.</p>
              </>
            ) : (
              <>
                <div
                  role="tablist"
                  aria-label="Tenant detail"
                  style={{ display: 'flex', gap: tokens.space[2] }}
                >
                  <ViewTab
                    label="Gateways"
                    active={tenantView === 'gateways'}
                    onClick={() => setTenantView('gateways')}
                  />
                  <ViewTab
                    label={`Audit${audit.length > 0 ? ` (${audit.length})` : ''}`}
                    active={tenantView === 'audit'}
                    onClick={() => setTenantView('audit')}
                  />
                </div>
                {tenantView === 'gateways' ? (
                  <GatewayList
                    gateways={gateways}
                    busy={busy}
                    onDecommission={(gid) =>
                      void act(() => decommissionGateway(opts(), selectedTenantId, gid))
                    }
                  />
                ) : (
                  <AuditPanel events={audit} />
                )}
              </>
            )}
          </div>
        </section>
      )}
    </main>
  );
}

function ViewTab({
  label,
  active,
  onClick,
}: {
  readonly label: string;
  readonly active: boolean;
  readonly onClick: () => void;
}): JSX.Element {
  return (
    <button
      type="button"
      role="tab"
      aria-selected={active}
      onClick={onClick}
      style={{
        background: active ? '#0f1620' : 'transparent',
        color: active ? tokens.color.fg : '#9aa4b2',
        border: `1px solid ${active ? tokens.color.accent : '#20242b'}`,
        borderRadius: 6,
        padding: `${tokens.space[1]}px ${tokens.space[3]}px`,
        cursor: 'pointer',
        fontSize: 13,
        fontWeight: 600,
      }}
    >
      {label}
    </button>
  );
}
