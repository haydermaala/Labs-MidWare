# Security Policy

lab-connect is safety-critical laboratory connectivity software. Security and
clinical safety are hard constraints, not features.

## Reporting a vulnerability

Do not open a public issue for security reports. Contact the project security
owner privately (contact channel: **OPEN — to be set by the project owner**).
Provide steps to reproduce, affected components, and impact. We aim to
acknowledge within a defined window (**OPEN — SLA to be set**).

## Scope and handling rules

- **Untrusted input everywhere.** Analyzer bytes, files, HL7/ASTM messages,
  driver packages, and cloud responses are treated as untrusted. Parsers must
  never panic, hang, or consume unbounded resources on malformed input.
- **No real patient data.** Repository, CI, fixtures, logs, screenshots, and
  diagnostic bundles contain synthetic or irreversibly de-identified data only.
- **No secrets in source.** Secrets never live in source, fixtures, logs, or
  bundles. `.env.example` holds names and safe defaults only. Secret scanning
  runs in CI.
- **Default redaction.** PHI and result values are redacted from logs, metrics,
  crash reports, and support bundles by default. Never place patient identifiers
  or result values in metric labels.
- **Passive by default.** Until a device profile is reviewed and validated, the
  gateway operates in passive capture mode and must not transmit to a device.
- **Least privilege.** Deny-by-default network, file, and device access;
  short-lived, rotated credentials; audited support access.
- **Signed drivers.** Driver packages are data-first, versioned, hash-verified,
  and signed, with revocation and rollback.

## Supported versions

Pre-1.0; no formal support matrix yet. See `docs/validation/` for the validation
lifecycle that gates any clinical claim.

## Regulatory note

Healthcare regulatory certification is a dedicated legal/quality workstream, not
a software checkbox. Compliance claims require qualified legal/quality review.
