import type { AuditEvent } from '@lab-connect/api-client';
import { tokens } from '@lab-connect/ui';

/** Format an ISO instant as a readable UTC string. */
function fmt(instant: string): string {
  const d = new Date(instant);
  return Number.isNaN(d.getTime()) ? instant : d.toISOString().replace('T', ' ').slice(0, 19) + 'Z';
}

const cell: React.CSSProperties = {
  padding: `${tokens.space[2]}px ${tokens.space[3]}px`,
  borderBottom: '1px solid #20242b',
  textAlign: 'left',
  fontSize: 13,
  verticalAlign: 'top',
};

// The API returns the append-only log oldest-first; show newest-first for monitoring.
export function AuditPanel({ events }: { readonly events: readonly AuditEvent[] }): JSX.Element {
  if (events.length === 0) {
    return <p style={{ color: '#9aa4b2' }}>No audit events for this tenant yet.</p>;
  }
  const newestFirst = events.slice().reverse();
  return (
    <table style={{ borderCollapse: 'collapse', width: '100%' }}>
      <thead>
        <tr>
          <th style={cell}>When</th>
          <th style={cell}>Event</th>
          <th style={cell}>Detail</th>
        </tr>
      </thead>
      <tbody>
        {newestFirst.map((e, i) => (
          <tr key={`${e.at}-${i}`}>
            <td style={{ ...cell, whiteSpace: 'nowrap', color: '#9aa4b2' }}>{fmt(e.at)}</td>
            <td style={cell}>
              <code style={{ fontSize: 12, color: tokens.color.fg }}>{e.kind}</code>
            </td>
            <td style={{ ...cell, color: '#c4c9d2', wordBreak: 'break-word' }}>{e.detail}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
