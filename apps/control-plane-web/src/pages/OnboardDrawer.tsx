// Gateway onboarding: issue a single-use enrollment token and walk the operator
// through installing gatewayd on the analyzer's on-site machine.
//
// The token is a bearer secret shown exactly once, so the drawer treats it like
// one — copy field, an explicit "issue" step, and a plain warning that it is not
// recoverable. No device credential or PHI is ever shown here.

import { useState } from 'react';
import { issueEnrollmentToken, type ControlPlaneOptions } from '@lab-connect/api-client';
import { Button, color, fontSize, space } from '@lab-connect/ui';
import { API_BASE } from '../config';
import { useAuth } from '../auth/AuthProvider';
import { Drawer, CopyField } from '../shell/Drawer';

function fleetOptions(token: string): ControlPlaneOptions {
  return { baseUrl: API_BASE, adminToken: token };
}

export function OnboardDrawer({ open, onClose, onEnrolled }: {
  readonly open: boolean;
  readonly onClose: () => void;
  readonly onEnrolled: () => void;
}): JSX.Element {
  const { token, activeTenantId } = useAuth();
  const [issued, setIssued] = useState<{ token: string; expiresAt: string } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  function close(): void {
    setIssued(null);
    setError(null);
    onClose();
  }

  async function issue(): Promise<void> {
    if (token === null || activeTenantId === null) {
      return;
    }
    setBusy(true);
    setError(null);
    try {
      const result = await issueEnrollmentToken(fleetOptions(token), activeTenantId);
      setIssued(result);
      onEnrolled(); // refresh the fleet list so the pending device appears once it enrolls
    } catch (e) {
      const status = (e as { status?: number }).status;
      setError(status === 401 ? 'You do not have permission to add gateways to this laboratory.'
        : 'Could not issue an enrollment token. Try again shortly.');
    } finally {
      setBusy(false);
    }
  }

  const expires = issued !== null ? new Date(issued.expiresAt) : null;

  return (
    <Drawer
      open={open}
      onClose={close}
      title="Add a gateway"
      description="Install the LabConnect gateway on the machine connected to the analyzer, then enroll it with a one-time token."
    >
      <div style={{ display: 'grid', gap: space[5] }}>
        <Step n={1} title="Install the gateway service">
          <p style={{ margin: 0, color: color.fgMuted, fontSize: fontSize.body }}>
            On the Windows or macOS machine on the analyzer's network, download and install the
            LabConnect gateway service (<code>gatewayd</code>). It makes only outbound connections and
            needs no inbound firewall ports.
          </p>
          <p style={{ margin: `${space[2]}px 0 0`, color: color.fgMuted, fontSize: fontSize.meta }}>
            Signed installers are distributed with the release; ask your administrator for the current
            download if you do not have it.
          </p>
        </Step>

        <Step n={2} title="Issue an enrollment token">
          {issued === null ? (
            <>
              <p style={{ margin: `0 0 ${space[3]}px`, color: color.fgMuted, fontSize: fontSize.body }}>
                Generate a single-use token to authorize this gateway. It is valid for 15 minutes and
                is shown only once.
              </p>
              {error !== null && (
                <p role="alert" style={{ margin: `0 0 ${space[3]}px`, color: color.danger, fontSize: fontSize.body }}>
                  {error}
                </p>
              )}
              <Button onClick={() => void issue()} loading={busy}>Issue enrollment token</Button>
            </>
          ) : (
            <div style={{ display: 'grid', gap: space[3] }}>
              <CopyField
                label="Enrollment token"
                value={issued.token}
                help={expires !== null
                  ? `Single-use · expires ${expires.toISOString().replace('T', ' ').slice(0, 16)}Z`
                  : 'Single-use'}
              />
              <p role="status" style={{
                margin: 0, padding: `${space[2]}px ${space[3]}px`, borderRadius: 4,
                fontSize: fontSize.meta, color: color.warn,
                border: `1px solid ${color.warn}`, background: 'color-mix(in oklch, var(--lc-warn) 8%, transparent)',
              }}>
                Copy this now — it cannot be shown again. If you lose it, issue a new one.
              </p>
            </div>
          )}
        </Step>

        <Step n={3} title="Enroll the gateway">
          <p style={{ margin: 0, color: color.fgMuted, fontSize: fontSize.body }}>
            In the gateway's setup, paste the control-plane address and the enrollment token. The
            gateway exchanges it for its own rotated device credential — the token is never reused.
          </p>
          <div style={{ marginTop: space[2] }}>
            <CopyField label="Control-plane address" value={API_BASE} />
          </div>
          <p style={{ margin: `${space[3]}px 0 0`, color: color.fgMuted, fontSize: fontSize.body }}>
            Once enrolled, the gateway appears in this fleet as <strong>never seen</strong> and turns
            <strong> online</strong> after its first heartbeat.
          </p>
        </Step>

        <div style={{ display: 'flex', justifyContent: 'end', gap: space[2], paddingTop: space[2] }}>
          <Button variant="secondary" onClick={close}>{issued !== null ? 'Done' : 'Cancel'}</Button>
        </div>
      </div>
    </Drawer>
  );
}

function Step({ n, title, children }: {
  readonly n: number; readonly title: string; readonly children: React.ReactNode;
}): JSX.Element {
  return (
    <section style={{ display: 'grid', gridTemplateColumns: 'auto 1fr', gap: space[3], alignItems: 'start' }}>
      <span aria-hidden="true" style={{
        width: 24, height: 24, borderRadius: 999, flexShrink: 0,
        background: color.surface2, color: color.fg, fontSize: fontSize.meta, fontWeight: 700,
        display: 'grid', placeItems: 'center',
      }}>{n}</span>
      <div style={{ display: 'grid', gap: space[2] }}>
        <h3 style={{ fontSize: fontSize.body, fontWeight: 600 }}>{title}</h3>
        {children}
      </div>
    </section>
  );
}
