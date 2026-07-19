import type { CapturedMessageMeta } from '@lab-connect/api-client';
import { tokens } from '@lab-connect/ui';

/**
 * Captured-message list. Capture-only view: shows metadata only — the raw
 * payload is never fetched or displayed (default redaction; payloads may contain
 * PHI).
 */
export function MessagesPanel({
  messages,
}: {
  messages: readonly CapturedMessageMeta[];
}): JSX.Element {
  const th: React.CSSProperties = {
    textAlign: 'left',
    padding: tokens.space[2],
    borderBottom: `1px solid #2a2f3a`,
    color: '#9aa4b2',
    fontWeight: 500,
  };
  const td: React.CSSProperties = { padding: tokens.space[2], borderBottom: `1px solid #1c2129` };

  return (
    <section aria-label="Captured messages" style={{ display: 'grid', gap: tokens.space[2] }}>
      <h2 style={{ margin: 0, fontSize: 16 }}>
        Captured messages{' '}
        <span style={{ fontSize: 12, color: '#9aa4b2' }}>
          — capture-only, payloads redacted
        </span>
      </h2>
      {messages.length === 0 ? (
        <p style={{ color: '#9aa4b2' }}>No messages captured yet.</p>
      ) : (
        <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13 }}>
          <thead>
            <tr>
              <th style={th}>Received (UTC)</th>
              <th style={th}>Transport</th>
              <th style={th}>Bytes</th>
              <th style={th}>Id</th>
            </tr>
          </thead>
          <tbody>
            {messages.map((m) => (
              <tr key={m.id}>
                <td style={td}>{m.received_at}</td>
                <td style={td}>{m.transport}</td>
                <td style={td}>{m.byte_len}</td>
                <td style={{ ...td, fontFamily: 'monospace', color: '#9aa4b2' }}>
                  {m.id.slice(0, 8)}…
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}
