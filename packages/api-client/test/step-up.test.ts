import { describe, it, expect, vi } from 'vitest';
import {
  ApiError,
  stepUp,
  decommissionGateway,
  type AuthOptions,
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

const base = 'https://api.example';

describe('step-up handling', () => {
  it('a 403 with stepUp surfaces the reason and requiresStepUp on ApiError', async () => {
    const f = mockFetch(403, {
      error: "'fleet.gateway.decommission' requires recent re-authentication (step-up)",
      stepUp: true,
    });
    const opts: ControlPlaneOptions = { baseUrl: base, adminToken: 'ses_x', fetchImpl: f };

    const err = await decommissionGateway(opts, 't1', 'g1').catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).status).toBe(403);
    expect((err as ApiError).requiresStepUp).toBe(true);
    expect((err as ApiError).reason).toContain('re-authentication');
  });

  it('a plain permission 403 (no role) is not flagged as step-up', async () => {
    const f = mockFetch(403, { error: "no role grants 'fleet.gateway.decommission'", stepUp: false });
    const opts: ControlPlaneOptions = { baseUrl: base, adminToken: 'ses_x', fetchImpl: f };

    const err = (await decommissionGateway(opts, 't1', 'g1').catch((e: unknown) => e)) as ApiError;
    expect(err.status).toBe(403);
    expect(err.requiresStepUp).toBe(false);
  });

  it('stepUp posts the password and MFA code to /api/auth/step-up', async () => {
    const f = mockFetch(204, null);
    const opts: AuthOptions = { baseUrl: base, sessionToken: 'ses_x', fetchImpl: f };

    await stepUp(opts, 'my-password', '123456');

    const [url, init] = f.mock.calls[0]!;
    expect(String(url)).toContain('/api/auth/step-up');
    expect(JSON.parse((init as RequestInit).body as string)).toEqual({ password: 'my-password', code: '123456' });
  });

  it('stepUp sends a null code when none is provided', async () => {
    const f = mockFetch(204, null);
    await stepUp({ baseUrl: base, sessionToken: 'ses_x', fetchImpl: f }, 'pw');

    const init = f.mock.calls[0]![1] as RequestInit;
    expect(JSON.parse(init.body as string)).toEqual({ password: 'pw', code: null });
  });
});
