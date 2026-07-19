# Security review checklist (pre-pilot)

- Status: Draft (Phase 11)
- Date: 2026-07-18

Gate before any pilot. Items marked ⛔ require the owner (external reviewer,
identities, or infrastructure) and cannot be completed autonomously.

## Automated (in CI today)
- [x] Secret scanning (gitleaks) + push protection on `main`
- [x] Dependency advisories + license policy (cargo-deny, pnpm audit, dotnet audit)
- [x] Parser fuzz-smoke on every PR (ASTM framing/records, HL7 messages, MLLP)
- [x] `#![forbid(unsafe_code)]` across edge crates
- [x] SBOM + provenance attestation on release

## Design controls (implemented)
- [x] Capture-only transports (no send path; compile-time)
- [x] Persist-before-process; provenance enforced by DB foreign key
- [x] Signed driver verification; revocation; verified install + rollback
- [x] Local API: loopback-only, bearer-token, redaction-safe (no payloads)
- [x] Tenant isolation; single-use bootstrap tokens; rotated device credentials
- [x] Default redaction; numeric-only metric labels

## To complete before pilot
- [ ] ⛔ Independent threat-model review (external reviewer)
- [ ] ⛔ Penetration test (edge, control plane, update channel)
- [ ] ⛔ Scheduled long-form fuzz campaigns (beyond per-PR smoke)
- [ ] ⛔ Installer/update review + signing (Windows Authenticode, Apple notarization)
- [ ] ⛔ At-rest encryption decision per environment (edge SQLite, backups, R2)
- [ ] ⛔ Certificate-compromise + credential-rotation exercise
- [ ] ⛔ DR/restore test; incident-response + vulnerability-disclosure runbooks
- [ ] ⛔ Support-access audit review; least-privilege service accounts on hosts

## Sign-off

Critical/high findings must be resolved or formally risk-accepted by the security
owner (**OPEN**) before pilot. This checklist does not itself authorize a pilot.
