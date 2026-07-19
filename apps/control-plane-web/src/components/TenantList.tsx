import type { Tenant } from '@lab-connect/api-client';
import { tokens } from '@lab-connect/ui';

export function TenantList({
  tenants,
  selectedId,
  busy,
  onSelect,
  onDeactivate,
  onReactivate,
}: {
  readonly tenants: readonly Tenant[];
  readonly selectedId: string | null;
  readonly busy: boolean;
  readonly onSelect: (tenantId: string) => void;
  readonly onDeactivate: (tenantId: string) => void;
  readonly onReactivate: (tenantId: string) => void;
}): JSX.Element {
  if (tenants.length === 0) {
    return <p style={{ color: '#9aa4b2' }}>No tenants yet.</p>;
  }
  return (
    <ul style={{ listStyle: 'none', margin: 0, padding: 0, display: 'grid', gap: tokens.space[2] }}>
      {tenants.map((t) => {
        const selected = t.id === selectedId;
        return (
          <li
            key={t.id}
            style={{
              border: `1px solid ${selected ? tokens.color.accent : '#20242b'}`,
              borderRadius: 8,
              padding: tokens.space[3],
              display: 'flex',
              alignItems: 'center',
              gap: tokens.space[3],
              background: selected ? '#0f1620' : 'transparent',
            }}
          >
            <button
              type="button"
              onClick={() => onSelect(t.id)}
              aria-pressed={selected}
              style={{
                flex: 1,
                textAlign: 'left',
                background: 'transparent',
                border: 'none',
                color: tokens.color.fg,
                cursor: 'pointer',
                padding: 0,
              }}
            >
              <div style={{ fontWeight: 600 }}>{t.name}</div>
              <div style={{ color: '#6b7280', fontSize: 11 }}>{t.id}</div>
            </button>

            <span
              style={{
                background: t.active ? '#13351f' : '#351313',
                color: t.active ? '#5fd08a' : '#f08a8a',
                padding: '2px 8px',
                borderRadius: 999,
                fontSize: 12,
                fontWeight: 600,
              }}
            >
              {t.active ? 'active' : 'inactive'}
            </span>

            {t.active ? (
              <button
                type="button"
                disabled={busy}
                onClick={() => onDeactivate(t.id)}
                style={actionStyle('#f0a35f')}
              >
                Deactivate
              </button>
            ) : (
              <button
                type="button"
                disabled={busy}
                onClick={() => onReactivate(t.id)}
                style={actionStyle(tokens.color.accent)}
              >
                Reactivate
              </button>
            )}
          </li>
        );
      })}
    </ul>
  );
}

function actionStyle(color: string): React.CSSProperties {
  return {
    background: 'transparent',
    color,
    border: `1px solid ${color}`,
    borderRadius: 6,
    padding: '4px 10px',
    cursor: 'pointer',
    fontSize: 12,
  };
}
