import type { GatewayStatus } from '@lab-connect/api-client';
import { tokens } from '@lab-connect/ui';

/** Read-only gateway status (mode, schema version, outbox, audit). PHI-free. */
export function StatusPanel({ status }: { status: GatewayStatus }): JSX.Element {
  const cell: React.CSSProperties = {
    padding: `${tokens.space[2]}px ${tokens.space[3]}px`,
    background: '#141821',
    borderRadius: 6,
  };
  return (
    <section aria-label="Gateway status" style={{ display: 'grid', gap: tokens.space[2] }}>
      <h2 style={{ margin: 0, fontSize: 16 }}>Gateway status</h2>
      <div
        style={{
          display: 'grid',
          gridTemplateColumns: 'repeat(3, minmax(0, 1fr))',
          gap: tokens.space[2],
        }}
      >
        <div style={cell}>
          Mode
          <div style={{ color: tokens.color.accent, fontWeight: 600 }}>{status.mode}</div>
        </div>
        <div style={cell}>
          Schema
          <div style={{ fontWeight: 600 }}>v{status.schema_version}</div>
        </div>
        <div style={cell}>
          Audit events
          <div style={{ fontWeight: 600 }}>{status.audit_events}</div>
        </div>
        <div style={cell}>
          Pending
          <div style={{ fontWeight: 600 }}>{status.outbox.pending}</div>
        </div>
        <div style={cell}>
          Delivered
          <div style={{ fontWeight: 600 }}>{status.outbox.delivered}</div>
        </div>
        <div style={cell}>
          Dead-letter
          <div style={{ fontWeight: 600, color: status.outbox.dead > 0 ? tokens.color.danger : undefined }}>
            {status.outbox.dead}
          </div>
        </div>
      </div>
    </section>
  );
}
