# Drivers

Data-first, signed, versioned driver packages. See `docs/product/driver-manifest-v0.1.md`
for the manifest proposal and `DEVELOPMENT_PLAN.md` §2.5.

- `generic-astm/` — generic ASTM profile (Phase 7).
- `generic-hl7/` — generic HL7 v2 profile (Phase 7).
- `examples/` — reference/example packages.

Driver status lifecycle: `draft → parsed → mapped → validated → certified → deprecated/revoked`.
A driver is never "clinically valid" on the basis of parsing or simulation alone.
