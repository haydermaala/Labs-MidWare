// Billing: the tenant's current plan, entitlements, and subscription state.
//
// Entitlements are computed on the server; this page only reflects them. No
// prices appear here — the catalog is entitlement scope only (the pricing gate).
// Checkout and the customer portal arrive in E2 behind the provider seam; until
// then this is a read-only view any member may see (the gateway quota affects
// everyone, so everyone can see how much headroom the laboratory has).

import { useCallback, useEffect, useState } from 'react';
import {
  billingPlans, tenantBilling,
  type AuthOptions, type BillingPlan, type TenantBilling,
} from '@lab-connect/api-client';
import { color, fontSize, space } from '@lab-connect/ui';
import { API_BASE } from '../config';
import { useAuth } from '../auth/AuthProvider';
import { PageHeader } from './Pages';

function opts(token: string): AuthOptions {
  return { baseUrl: API_BASE, sessionToken: token };
}

/** Human label + tone for a subscription status (no monetary meaning). */
const STATUS_TONE: Record<string, { tone: string; label: string }> = {
  trialing: { tone: 'info', label: 'Trial' },
  active: { tone: 'ok', label: 'Active' },
  past_due: { tone: 'warn', label: 'Past due' },
  canceled: { tone: 'danger', label: 'Canceled' },
};

function fmtDate(instant: string | null): string {
  if (instant === null) return '—';
  const d = new Date(instant);
  return Number.isNaN(d.getTime()) ? instant : d.toISOString().slice(0, 10);
}

function quotaLabel(quota: number): string {
  return quota < 0 ? 'Unlimited' : `${quota} gateway${quota === 1 ? '' : 's'}`;
}

export function BillingPage(): JSX.Element {
  const { token, activeTenantId } = useAuth();
  const [data, setData] = useState<TenantBilling | null>(null);
  const [plans, setPlans] = useState<readonly BillingPlan[]>([]);
  const [state, setState] = useState<'loading' | 'ready' | 'denied' | 'error'>('loading');

  const load = useCallback(async (): Promise<void> => {
    if (token === null || activeTenantId === null) return;
    try {
      const [billing, catalog] = await Promise.all([
        tenantBilling(opts(token), activeTenantId),
        billingPlans(opts(token)),
      ]);
      setData(billing);
      setPlans(catalog);
      setState('ready');
    } catch (e) {
      const status = (e as { status?: number }).status;
      setState(status === 401 || status === 403 ? 'denied' : 'error');
    }
  }, [token, activeTenantId]);

  useEffect(() => { void load(); }, [load]);

  return (
    <>
      <PageHeader
        title="Billing"
        description="Your laboratory's plan and what it entitles. Contact us to change plans."
      />

      {state === 'denied' ? (
        <p role="alert" style={{ color: color.danger }}>
          You do not have permission to view billing for this laboratory.
        </p>
      ) : state === 'error' ? (
        <p role="alert" style={{ color: color.danger }}>Could not load billing. Try again shortly.</p>
      ) : state === 'loading' || data === null ? (
        <div aria-hidden="true" style={{ display: 'grid', gap: space[2] }}>
          {[0, 1].map((i) => <div key={i} style={{ height: 96, borderRadius: 6, background: color.surface2 }} />)}
        </div>
      ) : (
        <div style={{ display: 'grid', gap: space[5] }}>
          <CurrentPlan data={data} />
          <PlanCatalog plans={plans} currentPlanId={data.entitlements.planId} />
        </div>
      )}
    </>
  );
}

function StatusPill({ status }: { readonly status: string }): JSX.Element {
  const s = STATUS_TONE[status] ?? { tone: 'neutral', label: status };
  return <span className={`lc-badge lc-badge--${s.tone}`} role="status">{s.label}</span>;
}

function CurrentPlan({ data }: { readonly data: TenantBilling }): JSX.Element {
  const { entitlements: e, subscription } = data;
  const rows: ReadonlyArray<readonly [string, React.ReactNode]> = [
    ['Plan', <strong key="p">{e.planName}</strong>],
    ['Status', <StatusPill key="s" status={e.status} />],
    ['Gateway allowance', quotaLabel(e.gatewayQuota)],
    ['Features', e.features.length === 0 ? 'Core connectivity' : e.features.join(', ')],
    ['Renews', fmtDate(e.currentPeriodEnd)],
  ];

  return (
    <section style={{ display: 'grid', gap: space[3] }}>
      <h2 style={{ fontSize: fontSize.section, fontWeight: 600 }}>Current plan</h2>
      <div className="lc-card" style={{ padding: space[4], display: 'grid', gap: space[3] }}>
        <dl style={{ margin: 0, display: 'grid', gridTemplateColumns: 'max-content 1fr', gap: `${space[2]}px ${space[4]}px`, alignItems: 'center' }}>
          {rows.map(([label, value]) => (
            <div key={label} style={{ display: 'contents' }}>
              <dt style={{ color: color.fgMuted, fontSize: fontSize.body }}>{label}</dt>
              <dd style={{ margin: 0, fontSize: fontSize.body }}>{value}</dd>
            </div>
          ))}
        </dl>
        {subscription?.cancelAtPeriodEnd === true && (
          <p role="status" style={{
            margin: 0, padding: `${space[2]}px ${space[3]}px`, borderRadius: 4,
            fontSize: fontSize.body, color: color.fgMuted,
            border: `1px solid ${color.border}`, background: color.surface1,
          }}>
            This subscription is set to cancel at the end of the current period. Your laboratory
            will return to the Trial allowance afterward.
          </p>
        )}
      </div>
    </section>
  );
}

function PlanCatalog({ plans, currentPlanId }: {
  readonly plans: readonly BillingPlan[];
  readonly currentPlanId: string;
}): JSX.Element {
  return (
    <section style={{ display: 'grid', gap: space[3] }}>
      <h2 style={{ fontSize: fontSize.section, fontWeight: 600 }}>Plans</h2>
      <p style={{ margin: 0, color: color.fgMuted, fontSize: fontSize.body }}>
        Plans differ by gateway allowance and features. To change plans, contact your LabConnect
        representative — self-service checkout is coming soon.
      </p>
      <div style={{ display: 'grid', gap: space[3], gridTemplateColumns: 'repeat(auto-fit, minmax(200px, 1fr))' }}>
        {plans.map((p) => {
          const current = p.id === currentPlanId;
          return (
            <div key={p.id} className="lc-card" style={{
              padding: space[4], display: 'grid', gap: space[2], alignContent: 'start',
              outline: current ? `2px solid ${color.primary}` : undefined,
            }}>
              <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: space[2] }}>
                <h3 style={{ fontSize: fontSize.body, fontWeight: 600 }}>{p.name}</h3>
                {current && <span className="lc-badge lc-badge--info" role="status">Current</span>}
              </div>
              <div style={{ fontSize: fontSize.body }}>{quotaLabel(p.gatewayQuota)}</div>
              <ul style={{ margin: 0, paddingLeft: space[4], color: color.fgMuted, fontSize: fontSize.meta }}>
                {p.features.length === 0
                  ? <li>Core connectivity</li>
                  : p.features.map((f) => <li key={f}>{f}</li>)}
              </ul>
            </div>
          );
        })}
      </div>
    </section>
  );
}
