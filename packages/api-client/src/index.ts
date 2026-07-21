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

/** An API error carrying the HTTP status and, when present, the server's reason
 * and whether the action is merely gated on step-up (re-authentication). */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
    readonly reason?: string,
    readonly requiresStepUp: boolean = false,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

/** Build an ApiError from a non-ok Response, parsing the JSON body
 * `{ error, stepUp }` when the server provides one (403s from the auth engine). */
export async function errorFrom(res: Response, path: string): Promise<ApiError> {
  let reason: string | undefined;
  let stepUp = false;
  try {
    const body = (await res.json()) as { error?: unknown; stepUp?: unknown };
    if (typeof body.error === 'string') {
      reason = body.error;
    }
    if (typeof body.stepUp === 'boolean') {
      stepUp = body.stepUp;
    }
  } catch {
    /* empty or non-JSON body — fall back to a generic message */
  }
  return new ApiError(res.status, reason ?? `request to ${path} failed: ${res.status}`, reason, stepUp);
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
    throw await errorFrom(res, path);
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
