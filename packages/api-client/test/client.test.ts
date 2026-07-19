import { describe, it, expect, vi } from 'vitest';
import {
  ApiError,
  getHealth,
  getRecentMessages,
  getStatus,
  type ClientOptions,
} from '../src/index';

function mockFetch(status: number, body: unknown) {
  return vi.fn(async () =>
    new Response(JSON.stringify(body), {
      status,
      headers: { 'content-type': 'application/json' },
    }),
  );
}

const base = 'http://127.0.0.1:7373';

describe('gateway api client', () => {
  it('getHealth does not send an Authorization header', async () => {
    const fetchImpl = mockFetch(200, {
      service: 'gatewayd',
      version: '0.1.0',
      status: 'ok',
      mode: 'passive_capture',
    });
    const opts: ClientOptions = { baseUrl: base, token: 't', fetchImpl };
    const health = await getHealth(opts);
    expect(health.status).toBe('ok');
    const init = fetchImpl.mock.calls[0]![1] as RequestInit;
    expect((init.headers as Record<string, string>).Authorization).toBeUndefined();
  });

  it('getStatus sends the bearer token and parses counts', async () => {
    const fetchImpl = mockFetch(200, {
      service: 'gatewayd',
      version: '0.1.0',
      mode: 'passive_capture',
      schema_version: 1,
      outbox: { pending: 2, delivered: 1, dead: 0 },
      audit_events: 5,
    });
    const status = await getStatus({ baseUrl: base, token: 'secret', fetchImpl });
    expect(status.outbox.pending).toBe(2);
    expect(status.mode).toBe('passive_capture');
    const init = fetchImpl.mock.calls[0]![1] as RequestInit;
    expect((init.headers as Record<string, string>).Authorization).toBe('Bearer secret');
  });

  it('getRecentMessages clamps the limit and returns metadata', async () => {
    const fetchImpl = mockFetch(200, [
      { id: 'abc', transport: 'astm', received_at: '2026-07-18T10:00:00Z', byte_len: 107 },
    ]);
    const rows = await getRecentMessages({ baseUrl: base, token: 't', fetchImpl }, 9999);
    expect(rows).toHaveLength(1);
    expect(rows[0]!.byte_len).toBe(107);
    // Requested 9999 → clamped to 100.
    const url = String(fetchImpl.mock.calls[0]![0]);
    expect(url).toContain('limit=100');
  });

  it('throws ApiError with the status on failure', async () => {
    const fetchImpl = mockFetch(401, { error: 'unauthorized' });
    await expect(getStatus({ baseUrl: base, token: 'bad', fetchImpl })).rejects.toMatchObject({
      name: 'ApiError',
      status: 401,
    });
    expect(ApiError).toBeDefined();
  });
});
