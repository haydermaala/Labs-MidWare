import type { GatewayStatusLabel } from '@lab-connect/api-client';

// Color-codes a gateway's derived liveness. Purely presentational.
const STYLES: Record<GatewayStatusLabel, { bg: string; fg: string; label: string }> = {
  online: { bg: '#13351f', fg: '#5fd08a', label: 'online' },
  offline: { bg: '#3a2410', fg: '#f0a35f', label: 'offline' },
  never: { bg: '#20242b', fg: '#9aa4b2', label: 'never seen' },
  decommissioned: { bg: '#351313', fg: '#f08a8a', label: 'decommissioned' },
};

export function StatusBadge({ status }: { status: GatewayStatusLabel }): JSX.Element {
  const s = STYLES[status];
  return (
    <span
      role="status"
      style={{
        background: s.bg,
        color: s.fg,
        padding: '2px 8px',
        borderRadius: 999,
        fontSize: 12,
        fontWeight: 600,
        whiteSpace: 'nowrap',
      }}
    >
      {s.label}
    </span>
  );
}
