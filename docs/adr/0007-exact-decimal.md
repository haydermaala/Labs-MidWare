# ADR 0007 — Exact decimals for clinical numeric values (rust_decimal)

- Status: Accepted (Phase 2)
- Date: 2026-07-18

## Context

Clinical numeric results must be represented and transported **exactly**. Binary
floating point (`f32`/`f64`) cannot represent many decimal fractions exactly and
silently loses trailing-zero scale (e.g. `1.200` vs `1.2`), which can change
clinical meaning and break reconciliation. The canonical model needs a decimal
type that (a) is exact, (b) preserves scale/trailing zeros, and (c) round-trips
losslessly through JSON contracts.

## Decision

Represent clinical numeric values with [`rust_decimal::Decimal`], aliased as
`canonical_model::DecimalValue`. Enable the crate's `serde-str` feature so decimals
serialize as **strings** (e.g. `"1.200"`), preserving exact value and scale across
JSON. Floating point is prohibited for clinical numeric values throughout the
codebase.

Timestamps use `time::OffsetDateTime` in UTC (RFC 3339 on the wire) — also no
floating point.

## Consequences

- Exact values and trailing zeros survive serialize/deserialize (covered by tests
  `decimal_preserves_scale_and_trailing_zeros`, `decimal_high_precision_roundtrips`).
- Downstream contracts (TS/`.NET`) must treat these fields as **strings**, not
  numbers, to preserve exactness. The TS `contracts` package will type them as
  `string`.
- `rust_decimal` supports up to 28–29 significant digits; values beyond that range
  must be handled as text (the `ResultValue::Text` variant) rather than truncated.

## Alternatives considered

- **f64**: rejected — inexact, loses scale, unsafe for clinical values.
- **Arbitrary-precision (bigdecimal)**: heavier; 28-digit precision is sufficient
  for laboratory values and `rust_decimal` is lighter and well-supported. Revisit
  via a superseding ADR only if a real assay needs more precision.
- **Store as raw string only**: loses arithmetic/validation ergonomics; we keep the
  original text in `ResultValue::Text` when a value is not safely decimal, but use
  an exact decimal type when it is.
