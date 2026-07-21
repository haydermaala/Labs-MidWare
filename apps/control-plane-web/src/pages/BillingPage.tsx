// Billing: the tenant's current plan, entitlements, and subscription state.
//
// Entitlements are computed on the server; this page only reflects them. No
// prices appear here — the catalog is entitlement scope only (the pricing gate).
// Checkout and the customer portal arrive in E2 behind the provider seam; until
// then this is a read-only view any member may see (the gateway quota affects
// everyone, so everyone can see how much headroom the laboratory has).

import { useCallback, useEffect, useState } from 'react';
import {
  billingPlans, openBillingPortal, startCheckout, tenantBilling,
  type AuthOptions, type BillingPlan, type TenantBilling,
} from '@lab-connect/api-client';
import { Button, color, fontSize, space } from '@lab-connect/ui';
import { API_BASE } from '../config';
import { useAuth } from '../auth/AuthProvider';
import { StepUpCancelledError, useStepUp } from '../auth/StepUpProvider';
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
  const { token, activeTenantId, activeRole } = useAuth();
  const { guard } = useStepUp();
  const canManage = activeRole === 'owner' || activeRole === 'billing-admin';
  const [data, setData] = useState<TenantBilling | null>(null);
  const [plans, setPlans] = useState<readonly BillingPlan[]>([]);
  const [state, setState] = useState<'loading' | 'ready' | 'denied' | 'error'>('loading');
  const [busy, setBusy] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

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

  /** Kicks off a provider redirect (checkout or portal), surfacing failures. */
  async function redirect(key: string, get: () => Promise<string>): Promise<void> {
    setBusy(key);
    setNotice(null);
    try {
      // Changing a plan is high-risk: prompts step-up re-auth if the session is stale.
      const url = await guard(get);
      window.location.assign(url);
    } catch (e) {
      if (e instanceof StepUpCancelledError) {
        setBusy(null);
        return; // operator dismissed the re-auth prompt
      }
      setNotice('That could not be started right now. Please try again shortly.');
      setBusy(null);
    }
  }

  const onCheckout = (planId: string): Promise<void> =>
    redirect(`checkout-${planId}`, () => startCheckout(opts(token!), activeTenantId!, planId));
  const onPortal = (): Promise<void> =>
    redirect('portal', () => openBillingPortal(opts(token!), activeTenantId!));

  return (
    <>
      <PageHeader
        title="Billing"
        description={canManage
          ? "Your laboratory's plan, what it entitles, and how to change it."
          : "Your laboratory's plan and what it entitles."}
      />

      {notice !== null && (
        <p role="alert" style={{
          margin: `0 0 ${space[4]}px`, padding: `${space[2]}px ${space[3]}px`, borderRadius: 4,
          color: color.danger, border: `1px solid ${color.danger}`,
          background: 'color-mix(in oklch, var(--lc-danger) 8%, transparent)', fontSize: fontSize.body,
        }}>{notice}</p>
      )}

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
          <CurrentPlan
            data={data}
            canManage={canManage}
            portalBusy={busy === 'portal'}
            onPortal={onPortal}
          />
          <PlanCatalog
            plans={plans}
            currentPlanId={data.entitlements.planId}
            canManage={canManage}
            busy={busy}
            onCheckout={onCheckout}
          />
        </div>
      )}
    </>
  );
}

function StatusPill({ status }: { readonly status: string }): JSX.Element {
  const s = STATUS_TONE[status] ?? { tone: 'neutral', label: status };
  return <span className={`lc-badge lc-badge--${s.tone}`} role="status">{s.label}</span>;
}

function CurrentPlan({ data, canManage, portalBusy, onPortal }: {
  readonly data: TenantBilling;
  readonly canManage: boolean;
  readonly portalBusy: boolean;
  readonly onPortal: () => Promise<void>;
}): JSX.Element {
  const { entitlements: e, subscription } = data;
  const rows: ReadonlyArray<readonly [string, React.ReactNode]> = [
    ['Plan', <strong key="p">{e.planName}</strong>],
    ['Status', <StatusPill key="s" status={e.status} />],
    ['Gateway allowance', quotaLabel(e.gatewayQuota)],
    ['Features', e.features.length === 0 ? 'Core connectivity' : e.features.join(', ')],
    ['Renews', fmtDate(e.currentPeriodEnd)],
  ];
  // The portal manages an existing paid subscription; there's nothing to manage
  // on the default Trial (no provider customer yet).
  const hasSubscription = subscription !== null;

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
        {canManage && hasSubscription && (
          <div>
            <Button variant="secondary" loading={portalBusy} onClick={() => void onPortal()}>
              Manage billing
            </Button>
          </div>
        )}
      </div>
    </section>
  );
}

function PlanCatalog({ plans, currentPlanId, canManage, busy, onCheckout }: {
  readonly plans: readonly BillingPlan[];
  readonly currentPlanId: string;
  readonly canManage: boolean;
  readonly busy: string | null;
  readonly onCheckout: (planId: string) => Promise<void>;
}): JSX.Element {
  return (
    <section style={{ display: 'grid', gap: space[3] }}>
      <h2 style={{ fontSize: fontSize.section, fontWeight: 600 }}>Plans</h2>
      <p style={{ margin: 0, color: color.fgMuted, fontSize: fontSize.body }}>
        Plans differ by gateway allowance and features. {canManage
          ? 'Choose a plan to start checkout.'
          : 'Ask an owner or billing administrator to change plans.'}
      </p>
      <div style={{ display: 'grid', gap: space[3], gridTemplateColumns: 'repeat(auto-fit, minmax(200px, 1fr))' }}>
        {plans.map((p) => {
          const current = p.id === currentPlanId;
          // Trial is the default fallback, not a purchasable plan.
          const purchasable = canManage && !current && p.id !== 'trial';
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
              {purchasable && (
                <div style={{ marginTop: space[1] }}>
                  <Button
                    variant="secondary"
                    loading={busy === `checkout-${p.id}`}
                    onClick={() => void onCheckout(p.id)}
                  >
                    Choose {p.name}
                  </Button>
                </div>
              )}
            </div>
          );
        })}
      </div>
    </section>
  );
}
