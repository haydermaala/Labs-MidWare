import { describe, it, expect, vi } from 'vitest';
import {
  ApiError,
  grantPlatformRole,
  listPlatformRoleAssignments,
  platformWhoami,
  provisionTenant,
  requestSupportGrant,
  revokePlatformRole,
  type ControlPlaneOptions,
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
  return { baseUrl: base, adminToken: 'session-token', fetchImpl };
}

function lastCall(fetchImpl: ReturnType<typeof vi.fn>): [URL, RequestInit] {
  const call = fetchImpl.mock.calls[0]!;
  return [call[0] as URL, call[1] as RequestInit];
}

describe('platform client', () => {
  it('reads the caller platform roles from whoami', async () => {
    const fetchImpl = mockFetch(200, { roles: ['platform-auditor'] });
    const who = await platformWhoami(opts(fetchImpl));
    const [url, init] = lastCall(fetchImpl);
    expect(url.pathname).toBe('/api/platform/whoami');
    expect((init.headers as Record<string, string>).Authorization).toBe('Bearer session-token');
    expect(who.roles).toEqual(['platform-auditor']);
  });

  it('grants a platform role with an optional reason', async () => {
    const fetchImpl = mockFetch(201, { id: 'pra_1', userId: 'u1', role: 'platform-auditor', active: true });
    await grantPlatformRole(opts(fetchImpl), { userId: 'u1', role: 'platform-auditor', reason: 'audit' });
    const [url, init] = lastCall(fetchImpl);
    expect(url.pathname).toBe('/api/platform/role-assignments');
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body as string)).toMatchObject({ userId: 'u1', role: 'platform-auditor', reason: 'audit' });
  });

  it('filters role assignments by user and DELETEs a revoke', async () => {
    const list = mockFetch(200, []);
    await listPlatformRoleAssignments(opts(list), 'u9');
    expect((lastCall(list)[0]).search).toContain('userId=u9');

    const del = mockFetch(204, null);
    await revokePlatformRole(opts(del), 'pra_x');
    const [url, init] = lastCall(del);
    expect(url.pathname).toBe('/api/platform/role-assignments/pra_x');
    expect(init.method).toBe('DELETE');
  });

  it('provisions a tenant and requests a support grant', async () => {
    const prov = mockFetch(201, { id: 'ten_1', name: 'Co', createdAt: 'x', active: true });
    await provisionTenant(opts(prov), 'Co');
    expect(lastCall(prov)[0].pathname).toBe('/api/platform/tenants');

    const sup = mockFetch(202, { id: 'psg_1', status: 'pending' });
    await requestSupportGrant(opts(sup), { subjectTenantId: 'ten_1', reason: 'incident', durationMinutes: 30 });
    const [url, init] = lastCall(sup);
    expect(url.pathname).toBe('/api/platform/support-grants');
    expect(JSON.parse(init.body as string)).toMatchObject({ subjectTenantId: 'ten_1', durationMinutes: 30 });
  });

  it('surfaces a 403 as an ApiError with the reason + step-up flag', async () => {
    const fetchImpl = vi.fn(async () =>
      new Response(JSON.stringify({ error: 'requires MFA', stepUp: true }), {
        status: 403, headers: { 'content-type': 'application/json' },
      }),
    );
    await expect(grantPlatformRole(opts(fetchImpl), { userId: 'u', role: 'platform-root-owner' }))
      .rejects.toMatchObject({ status: 403, reason: 'requires MFA', requiresStepUp: true } satisfies Partial<ApiError>);
  });
});
