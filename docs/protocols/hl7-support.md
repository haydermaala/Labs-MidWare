# HL7 v2 — supported versions and profiles

- Status: Draft (Phase 6, evolving)
- Date: 2026-07-18

> **We do not claim generic HL7 compatibility.** This document states exactly what
> is supported. Anything not listed is out of scope until explicitly added and
> tested. Clinical validity of any profile additionally requires the validation
> lifecycle (see `docs/validation/validation-strategy.md`).

## Parsing (this increment)

- **Structural, lossless** parse of any HL7 v2 message into segments → fields →
  repetitions → components → subcomponents, with raw bytes retained at every
  level and unknown segments/fields preserved.
- Delimiters are read from the `MSH` segment (`MSH-1` field separator, `MSH-2`
  encoding characters). Standard defaults `| ^ ~ \ &`.
- **No clinical interpretation** at this layer: no unit/status/terminology
  inference and no result release. Structural extraction only.
- Malformed input never panics (fuzz-smoke on every PR).

## Message types (profiles)

Support is added incrementally with explicit tests. Current status:

| Trigger | Direction | Status |
|---------|-----------|--------|
| `ORU^R01` (observation result) | inbound (analyzer → gateway) | **parse only** (structural); mapping/release not yet |
| `ORM^O01` / `OML^O21` (orders) | — | not yet |
| `ACK` (`MSA`) | outbound/inbound | not yet (next increment) |
| `QBP`/`RSP` (queries) | — | not yet |

## Segments recognized by helpers

Structural parsing handles **all** segments (unknown ones are preserved). Typed
convenience accessors are being added for: `MSH`, `PID`, `PV1`, `ORC`, `OBR`,
`OBX`, `SPM`, `SAC`, `MSA`, `ERR`.

## Transport

- **MLLP** (start block `0x0B`, end block `0x1C 0x0D`) listener/client with size,
  time, and connection limits — **next increment**.
- ACK modes (original vs enhanced) — **next increment**.

## Explicit limitations (v0.1)

- HL7 **escape sequences** (`\F\`, `\S\`, …) are not expanded; raw bytes are
  preserved and expansion is deferred to mapping.
- Segment grouping (e.g. `ORU` group structure `PID`/`ORC`/`OBR`/`OBX`) is not
  modeled as a tree; segments are a flat ordered list. Consumers walk them in
  order.
- No FHIR. No generic version negotiation. Version is read from `MSH-12` but not
  yet enforced against a supported set.
