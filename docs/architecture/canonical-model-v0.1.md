# Canonical Laboratory Model — v0.1 (proposal)

- Status: Draft (Phase 0) — proposal for approval before Phase 2 implementation
- Date: 2026-07-18

The canonical model is the single normalized representation every device message
is mapped into. It is deliberately lossless-adjacent: raw evidence is always
retained and referenced, and no clinical meaning is inferred without a validated
mapping.

> **Binding rule.** Every normalized `Result` must reference: the raw byte
> message, the parser/driver version, the mapping version, the validation
> decision, the delivery attempt, and the acknowledgement. Provenance is not
> optional.

## Design principles

- **Exact decimals only** for clinical numeric values — never IEEE floating point.
  Numeric results are stored as an exact decimal (value + scale) or as the
  original text when not safely numeric. **OPEN:** representation (e.g. Rust
  `rust_decimal` vs. a string-backed decimal newtype) decided in Phase 2.
- **Strongly typed IDs** (newtypes per entity) — no bare strings/ints crossing
  boundaries.
- **UTC timestamps** with source-offset preserved; capture time vs. result time
  distinguished.
- **Unknown-preserving**: unknown fields/components/records are retained verbatim,
  never silently dropped.
- **Coded values carry system + code + text** (do not collapse to text).
- **Status is explicit**, never guessed (see `ResultStatus`).

## Entity groups (from DEVELOPMENT_PLAN §2.4)

### Organizational
`Organization`, `Site`, `Laboratory`, `User`, `Role`.

### Fleet & connectivity
`Gateway`, `GatewayCertificate`, `DeviceInstance`, `ConnectionProfile`.

### Drivers
`Driver`, `DriverVersion`, `CompatibilityRule`, `Signature`.

### Clinical (synthetic-only in dev)
`PatientReference`, `Specimen`, `LabOrder`, `RequestedTest`,
`ResultSet`, `Result`, `ResultFlag`, `ReferenceRange`, `QCResult`.

### Mapping
`TestMapping`, `UnitMapping`, `TerminologyMapping`.

### Provenance & delivery
`RawMessage`, `ParsedMessage`, `TransformationRecord`,
`DeliveryAttempt`, `Acknowledgement`, `DeadLetter`.

### Validation & governance
`ValidationPlan`, `ValidationCase`, `ValidationRun`, `Approval`,
`AuditEvent`, `SecurityEvent`, `SoftwareRelease`.

## Key types (illustrative, not final)

| Type | Purpose | Notes |
|------|---------|-------|
| `MessageId`, `ResultId`, `DeviceInstanceId`, `DriverVersionId` … | strongly typed IDs | opaque, newtype per entity |
| `DecimalValue { unscaled, scale }` | exact numeric | no float; preserves trailing zeros |
| `ResultValue` = `Numeric(DecimalValue)` \| `Coded{system,code,text}` \| `Text(String)` \| `Absent{reason}` | result payload | never coerce across variants |
| `ResultStatus` = `Preliminary \| Final \| Corrected \| Cancelled \| Unknown` | explicit lifecycle | `Unknown` when source is ambiguous — never guessed to `Final` |
| `Provenance { raw_message, parser_version, driver_version, mapping_version, validation_decision, delivery, acknowledgement }` | chain of custody | attached to every `Result` |
| `OperatingMode` = `PassiveCapture \| Active` | device mode | defaults to `PassiveCapture` |

The `gateway-api` crate already encodes `OperatingMode` (default `PassiveCapture`)
and a PHI-free `Health` payload as the first slice of this model.

## Serialization & contracts

- JSON Schema / OpenAPI defines the wire contracts (`packages/validation-schemas`,
  `packages/contracts`); Rust and TS types are generated/aligned from them.
- Contract version is independent and asserted by consumers (`CONTRACT_VERSION`).
- Round-trip and backward-compatibility tests are required (Phase 2 acceptance).

## Open questions

- **OPEN:** exact-decimal library/representation.
- **OPEN:** how much of `PatientReference` is stored at the edge vs. tokenized
  (depends on data-classification + retention decisions, still OPEN).
- **OPEN:** terminology systems in scope (LOINC/UCUM/SNOMED) — deferred until a
  concrete integration requires them; no mapping is inferred for production.
- **OPEN:** `QCResult` shape (Phase 6+/QC is post-MVP).
