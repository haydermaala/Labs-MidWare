// People: tenant members and invitations.
//
// The server is the authority on every rule here (owner grants require an owner,
// the last owner cannot be demoted or removed). The UI mirrors those rules so
// operators are not offered actions that will fail, and surfaces the server's
// refusal verbatim when they are attempted anyway.

import { useCallback, useEffect, useState } from 'react';
import {
  changeMemberRole, inviteMember, listInvitations, listMembers, removeMember, revokeInvitation,
  type ControlPlaneOptions, type InvitationView, type Member,
} from '@lab-connect/api-client';
import { Button, Field, StatusBadge, color, fontSize, space } from '@lab-connect/ui';
import type { StatusKind } from '@lab-connect/ui';
import { API_BASE } from '../config';
import { useAuth } from '../auth/AuthProvider';
import { StepUpCancelledError, useStepUp } from '../auth/StepUpProvider';
import { PageHeader } from './Pages';

const ROLES = [
  'owner', 'tenant-admin', 'lab-admin', 'technician', 'mapping-reviewer',
  'clinical-approver', 'billing-admin', 'auditor', 'read-only',
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

export function PeoplePage(): JSX.Element {
  const { token, activeTenantId, activeRole, user } = useAuth();
  const { guard } = useStepUp();
  const isOwner = activeRole === 'owner';
  const canManage = isOwner || activeRole === 'tenant-admin';

  const [members, setMembers] = useState<readonly Member[]>([]);
  const [invitations, setInvitations] = useState<readonly InvitationView[]>([]);
  const [state, setState] = useState<'loading' | 'ready' | 'denied' | 'error'>('loading');
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  const load = useCallback(async (): Promise<void> => {
    if (token === null || activeTenantId === null) {
      return;
    }
    try {
      const [m, i] = await Promise.all([
        listMembers(opts(token), activeTenantId),
        listInvitations(opts(token), activeTenantId),
      ]);
      setMembers(m);
      setInvitations(i);
      setState('ready');
    } catch (e) {
      const status = (e as { status?: number }).status;
      setState(status === 401 || status === 403 ? 'denied' : 'error');
    }
  }, [token, activeTenantId]);

  useEffect(() => { void load(); }, [load]);

  /** Runs a mutation (prompting step-up re-auth if the server requires it),
   * surfacing the server's refusal reason when it declines. */
  async function run(key: string, action: () => Promise<void>): Promise<void> {
    setBusy(key);
    setNotice(null);
    try {
      await guard(action);
      await load();
    } catch (e) {
      if (e instanceof StepUpCancelledError) {
        return; // the operator dismissed the re-auth prompt; leave state as-is
      }
      const status = (e as { status?: number }).status;
      setNotice(
        status === 409
          ? 'A laboratory must keep at least one owner. Promote another member first.'
          : status === 403
            ? 'Only an owner can grant or revoke the owner role.'
            : 'That change could not be applied. Please try again.',
      );
    } finally {
      setBusy(null);
    }
  }

  if (!canManage) {
    return (
      <>
        <PageHeader title="People" description="Members and invitations for this laboratory." />
        <p role="alert" style={{ margin: 0, padding: space[4], borderRadius: 6, background: color.surface1,
          border: `1px solid ${color.border}`, color: color.fgMuted }}>
          You do not have permission to manage people in this laboratory. Ask an owner or tenant
          administrator.
        </p>
      </>
    );
  }

  const activeMembers = members.filter((m) => m.active);
  const ownerCount = activeMembers.filter((m) => m.role === 'owner').length;

  return (
    <>
      <PageHeader title="People" description="Members and invitations for this laboratory." />

      {notice !== null && (
        <p role="alert" style={{
          margin: `0 0 ${space[4]}px`, padding: `${space[2]}px ${space[3]}px`, borderRadius: 4,
          color: color.danger, border: `1px solid ${color.danger}`,
          background: 'color-mix(in oklch, var(--lc-danger) 8%, transparent)', fontSize: fontSize.body,
        }}>{notice}</p>
      )}

      {state === 'denied' ? (
        <p role="alert" style={{ color: color.danger }}>You do not have permission to view members.</p>
      ) : state === 'error' ? (
        <p role="alert" style={{ color: color.danger }}>Could not load people. Try again shortly.</p>
      ) : state === 'loading' ? (
        <div aria-hidden="true" style={{ display: 'grid', gap: space[2] }}>
          {[0, 1, 2].map((i) => <div key={i} style={{ height: 36, borderRadius: 4, background: color.surface2 }} />)}
        </div>
      ) : (
        <div style={{ display: 'grid', gap: space[5] }}>
          <section style={{ display: 'grid', gap: space[3] }}>
            <h2 style={{ fontSize: fontSize.section, fontWeight: 600 }}>
              Members <span style={{ color: color.fgMuted, fontWeight: 400 }}>({activeMembers.length})</span>
            </h2>
            <div className="lc-card" style={{ overflowX: 'auto' }}>
              <table style={{ borderCollapse: 'collapse', width: '100%', minWidth: 620 }}>
                <thead>
                  <tr>
                    <th scope="col" style={th}>Member</th>
                    <th scope="col" style={th}>Role</th>
                    <th scope="col" style={th}>Since</th>
                    <th scope="col" style={{ ...th, textAlign: 'right' }}>Actions</th>
                  </tr>
                </thead>
                <tbody>
                  {activeMembers.map((m) => {
                    // Mirror the server rules so we never offer a doomed action.
                    const touchesOwner = m.role === 'owner';
                    const lockedByOwnership = touchesOwner && !isOwner;
                    const lastOwner = touchesOwner && ownerCount <= 1;
                    return (
                      <tr key={m.userId}>
                        <td style={td}>
                          <div style={{ fontWeight: 600 }}>{m.email}</div>
                          {m.userId === user?.id && (
                            <div style={{ fontSize: 11, color: color.fgMuted }}>you</div>
                          )}
                        </td>
                        <td style={td}>
                          <label className="lc-sr-only" htmlFor={`role-${m.userId}`}>
                            Role for {m.email}
                          </label>
                          <select
                            id={`role-${m.userId}`}
                            className="lc-input"
                            value={m.role}
                            disabled={lockedByOwnership || lastOwner || busy !== null}
                            onChange={(e) => void run(`role-${m.userId}`,
                              () => changeMemberRole(opts(token!), activeTenantId!, m.userId, e.target.value))}
                          >
                            {ROLES.map((r) => (
                              <option key={r} value={r} disabled={r === 'owner' && !isOwner}>{r}</option>
                            ))}
                          </select>
                        </td>
                        <td style={{ ...td, whiteSpace: 'nowrap' }} className="lc-tabular">{fmtDate(m.since)}</td>
                        <td style={{ ...td, textAlign: 'right' }}>
                          <Button
                            variant="danger"
                            disabled={lockedByOwnership || lastOwner}
                            loading={busy === `remove-${m.userId}`}
                            title={lastOwner ? 'A laboratory must keep at least one owner' : undefined}
                            onClick={() => void run(`remove-${m.userId}`,
                              () => removeMember(opts(token!), activeTenantId!, m.userId))}
                          >
                            Remove
                          </Button>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          </section>

          <InviteSection
            isOwner={isOwner}
            invitations={invitations}
            busy={busy}
            onInvite={(email, role) => run('invite', async () => {
              const result = await inviteMember(opts(token!), activeTenantId!, email, role);
              if (!result.emailDelivered) {
                // Non-fatal: the invitation exists, but the operator must know the
                // email did not go out so they can re-send or share the link.
                setNotice(
                  `Invitation to ${result.invitation.email} was created, but the email could not be ` +
                  'sent right now. It is listed below — you can revoke and re-send it once email recovers.',
                );
              }
            })}
            onRevoke={(id) => run(`revoke-${id}`,
              () => revokeInvitation(opts(token!), activeTenantId!, id))}
          />
        </div>
      )}
    </>
  );
}

function InviteSection({ isOwner, invitations, busy, onInvite, onRevoke }: {
  readonly isOwner: boolean;
  readonly invitations: readonly InvitationView[];
  readonly busy: string | null;
  readonly onInvite: (email: string, role: string) => Promise<void>;
  readonly onRevoke: (id: string) => Promise<void>;
}): JSX.Element {
  const [email, setEmail] = useState('');
  const [role, setRole] = useState<string>('technician');

  async function submit(event: React.FormEvent): Promise<void> {
    event.preventDefault();
    await onInvite(email, role);
    setEmail('');
  }

  return (
    <section style={{ display: 'grid', gap: space[3] }}>
      <h2 style={{ fontSize: fontSize.section, fontWeight: 600 }}>Invitations</h2>

      <form
        onSubmit={(e) => void submit(e)}
        className="lc-card"
        style={{ padding: space[4], display: 'flex', gap: space[3], alignItems: 'end', flexWrap: 'wrap' }}
      >
        <div style={{ flex: '1 1 240px' }}>
          <Field
            label="Email address"
            type="email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            placeholder="colleague@laboratory.example"
            help="They must accept while signed in with this address."
            required
          />
        </div>
        <div className="lc-field" style={{ flex: '0 1 180px' }}>
          <label className="lc-field__label" htmlFor="invite-role">Role</label>
          <select
            id="invite-role"
            className="lc-input"
            value={role}
            onChange={(e) => setRole(e.target.value)}
          >
            {ROLES.map((r) => (
              <option key={r} value={r} disabled={r === 'owner' && !isOwner}>{r}</option>
            ))}
          </select>
        </div>
        <Button type="submit" loading={busy === 'invite'} disabled={email === ''}>Send invitation</Button>
      </form>

      {invitations.length === 0 ? (
        <p style={{ margin: 0, color: color.fgMuted, fontSize: fontSize.body }}>
          No invitations yet.
        </p>
      ) : (
        <div className="lc-card" style={{ overflowX: 'auto' }}>
          <table style={{ borderCollapse: 'collapse', width: '100%', minWidth: 620 }}>
            <thead>
              <tr>
                <th scope="col" style={th}>Invitee</th>
                <th scope="col" style={th}>Role</th>
                <th scope="col" style={th}>Status</th>
                <th scope="col" style={th}>Expires</th>
                <th scope="col" style={{ ...th, textAlign: 'right' }}>Actions</th>
              </tr>
            </thead>
            <tbody>
              {invitations.map((i) => (
                <tr key={i.id}>
                  <td style={td}>{i.email}</td>
                  <td style={td}>{i.role}</td>
                  <td style={td}><StatusBadge status={i.status as StatusKind} /></td>
                  <td style={{ ...td, whiteSpace: 'nowrap' }} className="lc-tabular">{fmtDate(i.expiresAt)}</td>
                  <td style={{ ...td, textAlign: 'right' }}>
                    {i.status === 'pending' ? (
                      <Button
                        variant="secondary"
                        loading={busy === `revoke-${i.id}`}
                        onClick={() => void onRevoke(i.id)}
                      >
                        Revoke
                      </Button>
                    ) : (
                      <span style={{ color: color.fgMuted, fontSize: fontSize.meta }}>—</span>
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
