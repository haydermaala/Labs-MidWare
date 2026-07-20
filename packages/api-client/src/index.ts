// Typed clients for the control-plane and gateway REST APIs.
// The gateway client (below) is read-only over the loopback, redaction-safe
// endpoints served by `gatewayd`; the control-plane client lives in
// ./control-plane and is re-exported at the end of this module.
import type { Health } from '@lab-connect/contracts';

/** Outbox delivery counts by state. */
export interface OutboxCounts {
  readonly pending: number;
  readonly delivered: number;
  readonly dead: number;
}

/** Gateway status payload (PHI-free). */
export interface GatewayStatus {
  readonly service: string;
  readonly version: string;
  readonly mode: string;
  readonly schema_version: number;
  readonly outbox: OutboxCounts;
  readonly audit_events: number;
}

/** Redaction-safe captured-message metadata (never includes the payload). */
export interface CapturedMessageMeta {
  readonly id: string;
  readonly transport: string;
  readonly received_at: string;
  readonly byte_len: number;
}

/** Options for the gateway client. */
export interface ClientOptions {
  /** Base URL of the gateway API, e.g. "http://127.0.0.1:7373". */
  readonly baseUrl: string;
  /** Bearer token; required for everything except `/health`. */
  readonly token?: string;
  /** Injectable fetch (for tests). */
  readonly fetchImpl?: typeof fetch;
}

/** An API error carrying the HTTP status. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

function authHeaders(opts: ClientOptions): HeadersInit {
  return opts.token ? { Authorization: `Bearer ${opts.token}` } : {};
}

async function getJson<T>(opts: ClientOptions, path: string, auth: boolean): Promise<T> {
  const f = opts.fetchImpl ?? fetch;
  const res = await f(new URL(path, opts.baseUrl), {
    headers: auth ? authHeaders(opts) : {},
  });
  if (!res.ok) {
    throw new ApiError(res.status, `request to ${path} failed: ${res.status}`);
  }
  return (await res.json()) as T;
}

/** Liveness check (unauthenticated). */
export function getHealth(opts: ClientOptions): Promise<Health> {
  return getJson<Health>(opts, '/health', false);
}

/** Gateway status (mode, schema version, outbox counts, audit count). */
export function getStatus(opts: ClientOptions): Promise<GatewayStatus> {
  return getJson<GatewayStatus>(opts, '/status', true);
}

/** Recent captured-message metadata (redaction-safe; no payloads). */
export function getRecentMessages(
  opts: ClientOptions,
  limit = 20,
): Promise<readonly CapturedMessageMeta[]> {
  const clamped = Math.max(1, Math.min(100, Math.trunc(limit)));
  return getJson<CapturedMessageMeta[]>(opts, `/messages/recent?limit=${clamped}`, true);
}

// Control-plane (fleet management) client.
export * from './control-plane';

// Identity (auth/session/MFA/membership) client.
export * from './auth';
