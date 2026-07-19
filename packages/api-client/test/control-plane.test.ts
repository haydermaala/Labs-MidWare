import { describe, it, expect, vi } from 'vitest';
import {
  ApiError,
  createTenant,
  deactivateTenant,
  decommissionGateway,
  listGateways,
  listTenants,
  reactivateTenant,
  type ControlPlaneOptions,
  type GatewaySummary,
} from '../src/index';

function mockFetch(status: number, body: unknown) {
  return vi.fn(async () =>
    new Response(status === 204 ? null : JSON.stringify(body), {
      status,
      headers: { 'content-type': 'application/json' },
    }),
  );
}

const base = 'https://cp.example';
function opts(fetchImpl: typeof fetch): ControlPlaneOptions {
  return { baseUrl: base, adminToken: 'admin-secret', fetchImpl };
}

function lastCall(fetchImpl: ReturnType<typeof vi.fn>): [URL, RequestInit] {
  const call = fetchImpl.mock.calls[0]!;
  return [call[0] as URL, call[1] as RequestInit];
}

describe('control-plane client', () => {
  it('sends the admin bearer token on management calls', async () => {
    const fetchImpl = mockFetch(200, []);
    await listTenants(opts(fetchImpl));
    const [url, init] = lastCall(fetchImpl);
    expect(url.pathname).toBe('/api/tenants');
    expect((init.headers as Record<string, string>).Authorization).toBe('Bearer admin-secret');
  });

  it('parses tenants including the active flag', async () => {
    const fetchImpl = mockFetch(200, [
      { id: 'ten_1', name: 'Lab A', createdAt: '2026-07-19T00:00:00Z', active: true },
      { id: 'ten_2', name: 'Lab B', createdAt: '2026-07-19T00:01:00Z', active: false },
    ]);
    const tenants = await listTenants(opts(fetchImpl));
    expect(tenants).toHaveLength(2);
    expect(tenants[1]!.active).toBe(false);
  });

  it('parses gateway liveness (status + lastSeenAt)', async () => {
    const rows: GatewaySummary[] = [
      {
        id: 'gw_1',
        tenantId: 'ten_1',
        name: 'edge-1',
        enrolledAt: '2026-07-19T00:00:00Z',
        active: true,
        lastSeenAt: '2026-07-19T00:05:00Z',
        status: 'online',
      },
    ];
    const fetchImpl = mockFetch(200, rows);
    const gws = await listGateways(opts(fetchImpl), 'ten_1');
    const [url] = lastCall(fetchImpl);
    expect(url.pathname).toBe('/api/tenants/ten_1/gateways');
    expect(gws[0]!.status).toBe('online');
    expect(gws[0]!.lastSeenAt).toBe('2026-07-19T00:05:00Z');
  });

  it('createTenant posts the name as JSON', async () => {
    const fetchImpl = mockFetch(201, {
      id: 'ten_9',
      name: 'New Lab',
      createdAt: '2026-07-19T00:00:00Z',
      active: true,
    });
    const tenant = await createTenant(opts(fetchImpl), 'New Lab');
    const [, init] = lastCall(fetchImpl);
    expect(init.method).toBe('POST');
    expect(init.body).toBe(JSON.stringify({ name: 'New Lab' }));
    expect((init.headers as Record<string, string>)['Content-Type']).toBe('application/json');
    expect(tenant.id).toBe('ten_9');
  });

  it('lifecycle actions POST to the right paths and tolerate 204', async () => {
    const deact = mockFetch(204, null);
    await deactivateTenant(opts(deact), 'ten_1');
    expect(lastCall(deact)[0].pathname).toBe('/api/tenants/ten_1/deactivate');
    expect(lastCall(deact)[1].method).toBe('POST');

    const react = mockFetch(204, null);
    await reactivateTenant(opts(react), 'ten_1');
    expect(lastCall(react)[0].pathname).toBe('/api/tenants/ten_1/reactivate');

    const decomm = mockFetch(204, null);
    await decommissionGateway(opts(decomm), 'ten_1', 'gw_1');
    expect(lastCall(decomm)[0].pathname).toBe('/api/tenants/ten_1/gateways/gw_1/decommission');
  });

  it('throws ApiError with the status on failure', async () => {
    const fetchImpl = mockFetch(401, { error: 'nope' });
    await expect(listTenants(opts(fetchImpl))).rejects.toBeInstanceOf(ApiError);
    await expect(listTenants(opts(fetchImpl))).rejects.toMatchObject({ status: 401 });
  });
});
