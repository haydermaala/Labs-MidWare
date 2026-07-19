# ADR 0011 — Signed, data-first drivers with a verified install lifecycle

- Status: Accepted (Phase 7)
- Date: 2026-07-18

## Context

Driver packages come from outside the trust boundary and drive how analyzer bytes
become clinical results. A tampered or spoofed driver could corrupt results. The
plan requires signed, versioned packages with verification, revocation, and
rollback, and that drivers be **data-first** (no arbitrary executable code).

## Decision

- **Data-first manifests.** A driver is a manifest (identity, compatibility,
  capabilities) plus declarative rules and fixtures. Capabilities default to the
  safe value: `outboundToDevice` and `advancedTransforms` are `false`. Outbound
  device commands remain runtime-gated and separately approved even if declared.
- **ed25519 signatures over a SHA-256 digest.** Packages are signed offline; the
  runtime only ever **verifies**. `verify()` checks, in order: algorithm, digest
  vs. actual bytes (tamper detection), key trust (`TrustStore`), key/digest
  revocation, then the signature. Any failure rejects the package.
- **Revocation** by signing key, package digest, or driver id.
- **Verified install registry.** Installation always verifies first; unsigned,
  untrusted, revoked, or tampered packages are never installed. Each driver id
  keeps an ordered version history so a bad upgrade can be **rolled back**. Every
  install/rollback/rejection is recorded in an append-only audit log.
- **Lifecycle gate.** `draft → parsed → mapped → validated → certified →
  deprecated/revoked`; only `certified` is production-eligible, and certification
  additionally requires exact models, a named vendor, and a signature (enforced
  in manifest validation). Certification is a **clinical** gate (validation
  lifecycle), never granted by software checks alone.

## Consequences

- A whole class of supply-chain attacks on drivers is blocked at install time.
- Rollback and revocation give an operational response to a bad or compromised
  driver.
- **OPEN:** signing-key custody, rotation, and the package container format are
  decided with the code-signing identities (still OPEN); the algorithm is pinned
  to ed25519 for now and can be extended via a superseding ADR.

## Alternatives considered

- **Executable/scripted drivers**: rejected — unacceptable attack surface;
  advanced transforms, if ever needed, run sandboxed and disabled by default.
- **RSA/X.509**: heavier; ed25519 is small, fast, and sufficient. Revisit if a
  customer PKI requires X.509.
