import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import type { AuditEvent, GatewaySummary, Tenant } from '@lab-connect/api-client';
import { App } from '../src/App';
import { StatusBadge } from '../src/components/StatusBadge';
import { GatewayList } from '../src/components/GatewayList';
import { TenantList } from '../src/components/TenantList';
import { AuditPanel } from '../src/components/AuditPanel';

const gateways: GatewaySummary[] = [
  {
    id: 'gw_online00001abc',
    tenantId: 'ten_1',
    name: 'edge-online',
    enrolledAt: '2026-07-19T10:00:00Z',
    active: true,
    lastSeenAt: '2026-07-19T11:00:00Z',
    status: 'online',
  },
  {
    id: 'gw_never00002abc',
    tenantId: 'ten_1',
    name: 'edge-never',
    enrolledAt: '2026-07-19T10:00:00Z',
    active: true,
    lastSeenAt: null,
    status: 'never',
  },
  {
    id: 'gw_decomm0003abc',
    tenantId: 'ten_1',
    name: 'edge-gone',
    enrolledAt: '2026-07-19T10:00:00Z',
    active: false,
    lastSeenAt: '2026-07-19T09:00:00Z',
    status: 'decommissioned',
  },
];

describe('control-plane fleet UI', () => {
  it('App shows the fleet title and a connect control', () => {
    const { container } = render(<App />);
    expect(container.textContent).toContain('control plane');
    expect(container.textContent).toContain('fleet');
    expect(screen.getByRole('button', { name: /connect/i })).toBeTruthy();
  });

  it('StatusBadge renders a human label per liveness state', () => {
    expect(render(<StatusBadge status="online" />).container.textContent).toContain('online');
    expect(render(<StatusBadge status="offline" />).container.textContent).toContain('offline');
    expect(render(<StatusBadge status="never" />).container.textContent).toContain('never seen');
    expect(render(<StatusBadge status="decommissioned" />).container.textContent).toContain(
      'decommissioned',
    );
  });

  it('GatewayList shows status, last-seen (— when never), and only enables active decommission', () => {
    const onDecommission = vi.fn();
    render(<GatewayList gateways={gateways} busy={false} onDecommission={onDecommission} />);

    // Never-seen gateway shows an em dash for last-seen.
    expect(screen.getByText('—')).toBeTruthy();
    expect(screen.getAllByText('online').length).toBeGreaterThan(0);

    const buttons = screen.getAllByRole('button', { name: /decommission/i });
    // Three gateways → three buttons; the decommissioned one is disabled.
    expect(buttons).toHaveLength(3);
    const disabled = buttons.filter((b) => (b as HTMLButtonElement).disabled);
    expect(disabled).toHaveLength(1);

    fireEvent.click(buttons.find((b) => !(b as HTMLButtonElement).disabled)!);
    expect(onDecommission).toHaveBeenCalledWith('gw_online00001abc');
  });

  it('GatewayList shows an empty state', () => {
    render(<GatewayList gateways={[]} busy={false} onDecommission={vi.fn()} />);
    expect(screen.getByText(/no gateways enrolled/i)).toBeTruthy();
  });

  it('TenantList toggles Deactivate/Reactivate by active state and fires callbacks', () => {
    const tenants: Tenant[] = [
      { id: 'ten_a', name: 'Lab A', createdAt: '2026-07-19T00:00:00Z', active: true },
      { id: 'ten_b', name: 'Lab B', createdAt: '2026-07-19T00:00:00Z', active: false },
    ];
    const onDeactivate = vi.fn();
    const onReactivate = vi.fn();
    const onSelect = vi.fn();
    render(
      <TenantList
        tenants={tenants}
        selectedId={null}
        busy={false}
        onSelect={onSelect}
        onDeactivate={onDeactivate}
        onReactivate={onReactivate}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: /deactivate/i }));
    expect(onDeactivate).toHaveBeenCalledWith('ten_a');
    fireEvent.click(screen.getByRole('button', { name: /reactivate/i }));
    expect(onReactivate).toHaveBeenCalledWith('ten_b');
    fireEvent.click(screen.getByRole('button', { name: /Lab A/ }));
    expect(onSelect).toHaveBeenCalledWith('ten_a');
  });

  it('AuditPanel shows events newest-first with kind and detail', () => {
    const events: AuditEvent[] = [
      { at: '2026-07-19T10:00:00Z', kind: 'tenant.created', tenantId: 'ten_1', detail: 'Lab A' },
      {
        at: '2026-07-19T10:05:00Z',
        kind: 'gateway.decommissioned',
        tenantId: 'ten_1',
        detail: 'gw_abc',
      },
    ];
    const { container } = render(<AuditPanel events={events} />);
    expect(container.textContent).toContain('tenant.created');
    expect(container.textContent).toContain('gateway.decommissioned');
    // Newest-first: the decommission (later) row precedes the created row.
    const body = container.textContent ?? '';
    expect(body.indexOf('gateway.decommissioned')).toBeLessThan(body.indexOf('tenant.created'));
  });

  it('AuditPanel shows an empty state', () => {
    render(<AuditPanel events={[]} />);
    expect(screen.getByText(/no audit events/i)).toBeTruthy();
  });
});
