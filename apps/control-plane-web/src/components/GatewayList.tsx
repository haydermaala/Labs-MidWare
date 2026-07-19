import type { GatewaySummary } from '@lab-connect/api-client';
import { tokens } from '@lab-connect/ui';
import { StatusBadge } from './StatusBadge';

/** Format an ISO instant as a readable UTC string, or an em dash if absent. */
function fmt(instant: string | null): string {
  if (instant === null) return '—';
  const d = new Date(instant);
  return Number.isNaN(d.getTime()) ? instant : d.toISOString().replace('T', ' ').slice(0, 19) + 'Z';
}

const cell: React.CSSProperties = {
  padding: `${tokens.space[2]}px ${tokens.space[3]}px`,
  borderBottom: '1px solid #20242b',
  textAlign: 'left',
  fontSize: 13,
};

export function GatewayList({
  gateways,
  onDecommission,
  busy,
}: {
  readonly gateways: readonly GatewaySummary[];
  readonly onDecommission: (gatewayId: string) => void;
  readonly busy: boolean;
}): JSX.Element {
  if (gateways.length === 0) {
    return <p style={{ color: '#9aa4b2' }}>No gateways enrolled for this tenant yet.</p>;
  }
  return (
    <table style={{ borderCollapse: 'collapse', width: '100%' }}>
      <thead>
        <tr>
          <th style={cell}>Gateway</th>
          <th style={cell}>Status</th>
          <th style={cell}>Last seen</th>
          <th style={cell}>Enrolled</th>
          <th style={cell} aria-label="actions" />
        </tr>
      </thead>
      <tbody>
        {gateways.map((g) => (
          <tr key={g.id}>
            <td style={cell}>
              <div style={{ fontWeight: 600 }}>{g.name}</div>
              <div style={{ color: '#6b7280', fontSize: 11 }}>{g.id.slice(0, 12)}…</div>
            </td>
            <td style={cell}>
              <StatusBadge status={g.status} />
            </td>
            <td style={cell}>{fmt(g.lastSeenAt)}</td>
            <td style={cell}>{fmt(g.enrolledAt)}</td>
            <td style={{ ...cell, textAlign: 'right' }}>
              <button
                type="button"
                disabled={!g.active || busy}
                onClick={() => onDecommission(g.id)}
                title={g.active ? 'Revoke credential and mark inactive' : 'Already decommissioned'}
                style={{
                  background: 'transparent',
                  color: g.active ? tokens.color.danger : '#4b5563',
                  border: `1px solid ${g.active ? tokens.color.danger : '#374151'}`,
                  borderRadius: 6,
                  padding: '4px 10px',
                  cursor: g.active && !busy ? 'pointer' : 'not-allowed',
                  fontSize: 12,
                }}
              >
                Decommission
              </button>
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
