// Typed API client. Phase 1 scaffold: exposes a health fetch helper only.
// Real endpoints are added as contracts stabilize (Phase 2 / Phase 6).
import type { Health } from '@lab-connect/contracts';

export interface ClientOptions {
  readonly baseUrl: string;
  readonly fetchImpl?: typeof fetch;
}

/** Fetch the health payload from a lab-connect service. */
export async function getHealth(opts: ClientOptions): Promise<Health> {
  const f = opts.fetchImpl ?? fetch;
  const res = await f(new URL('/health', opts.baseUrl));
  if (!res.ok) {
    throw new Error(`health check failed: ${res.status}`);
  }
  return (await res.json()) as Health;
}
