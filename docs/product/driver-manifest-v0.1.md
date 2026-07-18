# Driver Manifest — v0.1 (proposal)

- Status: Draft (Phase 0) — proposal for approval before Phase 7 implementation
- Date: 2026-07-18

Drivers are **data-first**: declarative packages, not executable code. Advanced
transformations, if ever needed, run in a sandbox with strict CPU/memory/I/O/time
limits and are disabled by default. Packages are versioned, hash-verified, and
signed, with revocation and rollback.

## Package contents (from DEVELOPMENT_PLAN §2.5)

1. **Manifest** — identity + compatibility + capabilities (schema below).
2. **Session/framing rules** — declarative link-layer + framing parameters.
3. **Record/field extraction rules** — declarative, data-first.
4. **Mapping defaults + terminology hints** — never auto-activated for production.
5. **Golden fixtures** + expected canonical outputs (synthetic).
6. **Negative fixtures** + expected failures (synthetic).
7. **Simulator scenarios**.
8. **Known limitations** + site-validation checklist.
9. **Package digest + signature**.

## Manifest schema (v0.1, illustrative)

```jsonc
{
  "schemaVersion": "0.1",
  "id": "generic-astm",                     // stable driver id
  "vendor": "OPEN",                          // OPEN until a real device is scoped
  "models": ["*"],                           // exact models for certified drivers
  "firmwareRange": { "min": null, "max": null },
  "protocolFamily": "astm",                 // astm | hl7v2 | delimited | fixed-width
  "transports": ["serial", "tcp", "file"],
  "workflows": ["results-inbound"],          // results | orders | queries | qc ...
  "minimumRuntime": "0.1.0",                 // min gateway/driver-runtime version
  "status": "draft",                         // see lifecycle below
  "capabilities": {
    "outboundToDevice": false,               // MUST be false unless separately approved + allowlisted
    "advancedTransforms": false              // sandbox disabled by default
  },
  "signature": {
    "digest": "sha256:...",
    "algorithm": "OPEN",                     // signing scheme decided with signing identities (OPEN)
    "keyId": "OPEN"
  }
}
```

The JSON Schema id is registered in `packages/validation-schemas`
(`DRIVER_MANIFEST_SCHEMA_ID`). Manifests are validated with JSON Schema **and**
semantic rules (e.g. certified status requires exact models + firmware range +
signed golden/negative fixtures).

## Status lifecycle

`draft → parsed → mapped → validated → certified → deprecated/revoked`

| Status | Meaning | Can enter production? |
|--------|---------|-----------------------|
| draft | manifest exists, unproven | no |
| parsed | parses recorded synthetic samples | no |
| mapped | canonical mapping defined | no |
| validated | passes conformance + fault tests | no (technical only) |
| certified | controlled clinical validation signed off | yes, site-limited |
| deprecated / revoked | withdrawn or tampered/compromised | no |

Certification is a **clinical** gate (see `docs/validation/validation-strategy.md`),
not a software-test outcome. Parsing/simulation success never confers clinical
validity.

## Verification, revocation, rollback

- Package digest verified before install; signature verified against a trusted key.
- Tampered packages are rejected and raise a `SecurityEvent`.
- Installations are audited; previous version is retained for rollback.
- Revocation list checked at install and (where feasible) at runtime.

## Safety constraints

- `outboundToDevice` defaults to `false`. Enabling device commands requires a
  separate, recorded approval and a capability allowlist — never inferred.
- Mapping defaults are hints only; production mapping requires dual review
  (mapping reviewer + clinical approver).

## Open questions

- **OPEN:** signing scheme + key custody (tied to code-signing identities, OPEN).
- **OPEN:** package container format (e.g. signed tarball vs. OCI artifact).
- **OPEN:** revocation distribution mechanism (online vs. bundled list).
- **OPEN:** inheritance/override semantics between driver versions (Phase 7 detail).
