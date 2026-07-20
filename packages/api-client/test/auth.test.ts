import { describe, it, expect, vi } from 'vitest';
import {
  ApiError,
  login,
  verifyMfa,
  forgotPassword,
  myMemberships,
  acceptInvitation,
  type AuthOptions,
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
const session: AuthOptions = { baseUrl: base, sessionToken: 'ses_abc', fetchImpl: fetch };

describe('auth client', () => {
  it('login discriminates a plain session from an MFA challenge', async () => {
    const plain = mockFetch(200, {
      sessionToken: 'ses_x', expiresAt: '2026-07-27T00:00:00Z',
      user: { id: 'usr_1', email: 'a@b.co', emailVerified: true, active: true, mfaEnabled: false },
    });
    const outcome = await login({ baseUrl: base, fetchImpl: plain }, 'a@b.co', 'pw pw pw pw pw');
    expect(outcome.kind).toBe('session');

    const challenged = mockFetch(200, { mfaRequired: true, mfaToken: 'mtk_1' });
    const mfa = await login({ baseUrl: base, fetchImpl: challenged }, 'a@b.co', 'pw pw pw pw pw');
    expect(mfa).toEqual({ kind: 'mfa', mfaToken: 'mtk_1' });
  });

  it('verifyMfa posts token+code and returns the session', async () => {
    const fetchImpl = mockFetch(200, {
      sessionToken: 'ses_y', expiresAt: '2026-07-27T00:00:00Z',
      user: { id: 'usr_1', email: 'a@b.co', emailVerified: true, active: true, mfaEnabled: true },
    });
    const result = await verifyMfa({ baseUrl: base, fetchImpl }, 'mtk_1', '123456');
    expect(result.sessionToken).toBe('ses_y');
    const [url, init] = fetchImpl.mock.calls[0]! as unknown as [URL, RequestInit];
    expect(url.pathname).toBe('/api/auth/mfa/verify');
    expect(init.body).toBe(JSON.stringify({ mfaToken: 'mtk_1', code: '123456' }));
  });

  it('authenticated calls send the Bearer session token', async () => {
    const fetchImpl = mockFetch(200, []);
    await myMemberships({ ...session, fetchImpl });
    const [, init] = fetchImpl.mock.calls[0]! as unknown as [URL, RequestInit];
    expect((init.headers as Record<string, string>).Authorization).toBe('Bearer ses_abc');
  });

  it('forgotPassword resolves on 202 with no body', async () => {
    const fetchImpl = vi.fn(async () => new Response(null, { status: 202 }));
    await expect(forgotPassword({ baseUrl: base, fetchImpl }, 'ghost@b.co')).resolves.toBeUndefined();
  });

  it('acceptInvitation surfaces ApiError with status on failure', async () => {
    const fetchImpl = mockFetch(400, { error: 'invalid' });
    await expect(acceptInvitation({ ...session, fetchImpl }, 'inv_x')).rejects.toMatchObject(
      { status: 400 } satisfies Partial<ApiError>,
    );
  });
});
