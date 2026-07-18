// Shared contract surface. Phase 1 scaffold: version marker + operating-mode
// enum that mirrors the Rust gateway-api. Full DTOs generated from OpenAPI/JSON
// Schema in Phase 2.

/** Contract schema version. Bump via ADR; consumers assert compatibility. */
export const CONTRACT_VERSION = '0.1.0' as const;

/** Gateway operating mode. Default is passive per the safety boundary. */
export type OperatingMode = 'passive_capture' | 'active';

/** Minimal, PHI-free health payload shape shared across services. */
export interface Health {
  readonly service: string;
  readonly version: string;
  readonly status: string;
}
