// Shared contract surface — TypeScript mirror of the Rust `canonical-model` crate.
// The JSON Schema in `@lab-connect/validation-schemas` is the authoritative
// contract; these types mirror it for developer ergonomics and must stay in sync.

/** Contract schema version. Bump via ADR; consumers assert compatibility. */
export const CONTRACT_VERSION = '0.1.0' as const;

/**
 * Exact decimal encoded as a string (e.g. "5.30"). NEVER a JS `number` — that
 * would be an IEEE float and lose exactness/scale (see ADR 0007).
 */
export type Decimal = string;

/** RFC 3339 UTC instant, e.g. "2026-07-18T10:15:00Z". */
export type Timestamp = string;

/** A UUID string. */
export type Uuid = string;

/** Gateway operating mode. Default is passive per the safety boundary. */
export type OperatingMode = 'passive_capture' | 'active';

/** Minimal, PHI-free health payload shape shared across services. */
export interface Health {
  readonly service: string;
  readonly version: string;
  readonly status: string;
}

/** A coded concept; source coding preserved. */
export interface Coded {
  readonly system: string;
  readonly code: string;
  readonly text?: string;
}

/** Why a value is absent. */
export type AbsentReason =
  | 'not_reported'
  | 'below_detection_limit'
  | 'above_detection_limit'
  | 'pending'
  | 'unknown';

/** A result value — a discriminated union tagged by `kind`. Never coerced. */
export type ResultValue =
  | { readonly kind: 'numeric'; readonly value: Decimal; readonly unit?: string }
  | { readonly kind: 'coded'; readonly coded: Coded }
  | { readonly kind: 'text'; readonly text: string }
  | { readonly kind: 'absent'; readonly reason: AbsentReason };

/** Lifecycle status; `unknown` is explicit, never guessed. */
export type ResultStatus =
  | 'preliminary'
  | 'final'
  | 'corrected'
  | 'cancelled'
  | 'unknown';

/** Validation decision; nothing is releasable unless `released`. */
export type ValidationDecision =
  | 'pending_review'
  | 'held'
  | 'released'
  | 'rejected';

/** Reference range as reported by the source. */
export interface ReferenceRange {
  readonly low?: Decimal;
  readonly high?: Decimal;
  readonly text?: string;
}

/** Chain of custody attached to every result. */
export interface Provenance {
  readonly raw_message: Uuid;
  readonly parser_version: string;
  readonly driver_version?: Uuid;
  readonly mapping_version?: Uuid;
  readonly validation: ValidationDecision;
  readonly delivery?: Uuid;
  readonly acknowledgement?: Uuid;
}

/** A single normalized result. */
export interface Result {
  readonly id: Uuid;
  readonly test: Coded;
  readonly value: ResultValue;
  readonly status: ResultStatus;
  readonly flags?: readonly string[];
  readonly reference_range?: ReferenceRange;
  readonly observed_at?: Timestamp;
  readonly provenance: Provenance;
}

/** A set of results reported together. */
export interface ResultSet {
  readonly id: Uuid;
  readonly specimen?: Uuid;
  readonly results: readonly Result[];
}
