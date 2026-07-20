// Settings: the tenant's general information and lifecycle.
//
// Reading is open to any member; renaming and deactivating are owner-only, and
// the UI reflects that (inputs disabled, destructive action behind a typed
// confirmation). Deactivating is framed honestly — it stops new enrollment but
// retains data and audit, and the current members keep their access.

import { useCallback, useEffect, useState } from 'react';
import {
  deactivateTenant, getTenantSettings, reactivateTenant, renameTenant,
  type ControlPlaneOptions, type Tenant,
} from '@lab-connect/api-client';
import { Button, Field, StatusBadge, color, fontSize, space } from '@lab-connect/ui';
import { API_BASE } from '../config';
import { useAuth } from '../auth/AuthProvider';
import { PageHeader } from './Pages';

function opts(token: string): ControlPlaneOptions {
  return { baseUrl: API_BASE, adminToken: token };
}

export function SettingsPage(): JSX.Element {
  const { token, activeTenantId, activeRole, refresh } = useAuth();
  const isOwner = activeRole === 'owner';

  const [tenant, setTenant] = useState<Tenant | null>(null);
  const [state, setState] = useState<'loading' | 'ready' | 'denied' | 'error' | 'no-tenant'>('loading');
  const [name, setName] = useState('');
  const [nameError, setNameError] = useState<string | null>(null);
  const [savingName, setSavingName] = useState(false);
  const [saved, setSaved] = useState(false);
  const [confirmText, setConfirmText] = useState('');
  const [busyLifecycle, setBusyLifecycle] = useState(false);

  const load = useCallback(async (): Promise<void> => {
    if (token === null || activeTenantId === null) {
      setState('no-tenant');
      return;
    }
    try {
      const t = await getTenantSettings(opts(token), activeTenantId);
      setTenant(t);
      setName(t.name);
      setState('ready');
    } catch (e) {
      const status = (e as { status?: number }).status;
      setState(status === 401 || status === 403 ? 'denied' : 'error');
    }
  }, [token, activeTenantId]);

  useEffect(() => { void load(); }, [load]);

  async function saveName(event: React.FormEvent): Promise<void> {
    event.preventDefault();
    const trimmed = name.trim();
    if (trimmed.length < 2 || trimmed.length > 120) {
      setNameError('Name must be 2 to 120 characters.');
      return;
    }
    if (token === null || activeTenantId === null) return;
    setSavingName(true);
    setNameError(null);
    setSaved(false);
    try {
      const updated = await renameTenant(opts(token), activeTenantId, trimmed);
      setTenant(updated);
      setSaved(true);
      await refresh(); // header/switcher show the new name
    } catch {
      setNameError('Could not rename the laboratory. Try again shortly.');
    } finally {
      setSavingName(false);
    }
  }

  async function toggleActive(): Promise<void> {
    if (token === null || activeTenantId === null || tenant === null) return;
    setBusyLifecycle(true);
    try {
      const updated = tenant.active
        ? (await deactivateTenant(opts(token), activeTenantId), false)
        : (await reactivateTenant(opts(token), activeTenantId), true);
      setTenant({ ...tenant, active: updated });
      setConfirmText('');
      await refresh();
    } finally {
      setBusyLifecycle(false);
    }
  }

  if (state === 'no-tenant') {
    return (
      <>
        <PageHeader title="Settings" description="General settings for this laboratory." />
        <p style={{ color: color.fgMuted }}>Select a laboratory to view its settings.</p>
      </>
    );
  }
  if (state === 'loading') {
    return (
      <>
        <PageHeader title="Settings" description="General settings for this laboratory." />
        <div aria-hidden="true" style={{ height: 120, borderRadius: 6, background: color.surface2 }} />
      </>
    );
  }
  if (state !== 'ready' || tenant === null) {
    return (
      <>
        <PageHeader title="Settings" description="General settings for this laboratory." />
        <p role="alert" style={{ color: color.danger }}>
          {state === 'denied' ? 'You do not have permission to view these settings.' : 'Could not load settings.'}
        </p>
      </>
    );
  }

  const created = new Date(tenant.createdAt);

  return (
    <>
      <PageHeader title="Settings" description="General settings for this laboratory." />
      <div style={{ display: 'grid', gap: space[4], maxWidth: 720 }}>
        <section className="lc-card" style={{ padding: space[4], display: 'grid', gap: space[3] }}>
          <h2 style={{ fontSize: fontSize.section, fontWeight: 600 }}>General</h2>
          <form onSubmit={(e) => void saveName(e)} style={{ display: 'grid', gap: space[3] }}>
            <Field
              label="Laboratory name"
              value={name}
              onChange={(e) => { setName(e.target.value); setSaved(false); }}
              disabled={!isOwner}
              maxLength={120}
              required
              {...(nameError !== null ? { error: nameError } : {})}
              {...(isOwner ? {} : { help: 'Only an owner can rename this laboratory.' })}
            />
            {isOwner && (
              <div style={{ display: 'flex', alignItems: 'center', gap: space[3] }}>
                <Button type="submit" loading={savingName} disabled={name.trim() === tenant.name}>Save changes</Button>
                {saved && <span role="status" style={{ color: color.ok, fontSize: fontSize.meta }}>Saved</span>}
              </div>
            )}
          </form>

          <dl style={{ margin: `${space[2]}px 0 0`, display: 'grid', gridTemplateColumns: 'auto 1fr', gap: `${space[2]}px ${space[4]}px`, fontSize: fontSize.body }}>
            <dt style={{ color: color.fgMuted }}>Laboratory ID</dt>
            <dd className="lc-mono" style={{ margin: 0 }}>{tenant.id}</dd>
            <dt style={{ color: color.fgMuted }}>Status</dt>
            <dd style={{ margin: 0 }}><StatusBadge status={tenant.active ? 'active' : 'inactive'} /></dd>
            <dt style={{ color: color.fgMuted }}>Created</dt>
            <dd className="lc-tabular" style={{ margin: 0 }}>
              {Number.isNaN(created.getTime()) ? tenant.createdAt : created.toISOString().slice(0, 10)}
            </dd>
          </dl>
        </section>

        {isOwner && (
          <section className="lc-card" style={{ padding: space[4], display: 'grid', gap: space[3], borderColor: color.danger }}>
            <h2 style={{ fontSize: fontSize.section, fontWeight: 600 }}>
              {tenant.active ? 'Deactivate laboratory' : 'Reactivate laboratory'}
            </h2>
            {tenant.active ? (
              <>
                <p style={{ margin: 0, color: color.fgMuted, fontSize: fontSize.body }}>
                  Deactivating stops new gateway enrollment. Existing gateways, configuration, and the
                  audit trail are retained, and current members keep their access. You can reactivate
                  at any time.
                </p>
                <Field
                  label={`Type the laboratory name to confirm`}
                  value={confirmText}
                  onChange={(e) => setConfirmText(e.target.value)}
                  placeholder={tenant.name}
                />
                <div>
                  <Button
                    variant="danger"
                    loading={busyLifecycle}
                    disabled={confirmText.trim() !== tenant.name}
                    onClick={() => void toggleActive()}
                  >
                    Deactivate laboratory
                  </Button>
                </div>
              </>
            ) : (
              <>
                <p style={{ margin: 0, color: color.fgMuted, fontSize: fontSize.body }}>
                  This laboratory is deactivated. Reactivate it to resume gateway enrollment.
                </p>
                <div><Button loading={busyLifecycle} onClick={() => void toggleActive()}>Reactivate laboratory</Button></div>
              </>
            )}
          </section>
        )}
      </div>
    </>
  );
}
