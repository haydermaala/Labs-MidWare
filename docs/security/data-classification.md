# Data Classification — lab-connect Laboratory Analyzer Middleware

Status: Draft (Phase 0)
Date: 2026-07-18

> This is a Phase 0 planning artifact. It defines the data classes lab-connect
> handles and the handling rules already mandated by `DEVELOPMENT_PLAN.md`
> (§2.3, §6, §7, §8) and the README safety boundary. It does not assert any
> regulatory or compliance status, does not claim conformance to any standard,
> and does not state which privacy law applies. Undecided items are marked
> **OPEN:**.

---

## 1. Purpose and core rules

Every piece of data in lab-connect is assigned a class. The class determines
where it may live, how it is protected, how long it is kept, and how it is
redacted. Two rules override everything else:

- **Never put patient identifiers or result values in metric labels** (or any
  telemetry stream, log line, crash report, screenshot, or support bundle by
  default). Operational telemetry must be PHI-free (§2.3, §6, §8).
- **Synthetic data only in the repository and CI.** All repository/CI fixtures
  are synthetic or irreversibly de-identified; no production patient data, no
  confidential vendor documents, no secrets committed anywhere (§1.2, §3.1,
  §5.2, §6).

Clinical validity is never conferred by parsing or simulation alone; a "parsed"
result is not a validated clinical result.

Storage locations referenced below:

- **Edge SQLite** — on-gateway durable store (raw messages, outbox, dedupe keys,
  audit; append-oriented raw store with encryption support).
- **PostgreSQL** — control-plane database (tenant, config, mappings, audit,
  fleet).
- **R2** — Cloudflare object storage (signed driver packages, synthetic
  fixtures, release assets, approved diagnostic bundles; per-env buckets).
- **Logs / metrics / support bundles** — operational surfaces that must stay
  PHI- and secret-free.

---

## 2. Data classes

### Class A — PHI / Clinical results (Highest)

- **Definition:** Patient-identifiable information and clinical result content:
  the canonical clinical payload and anything that identifies a patient,
  specimen, or order.
- **Examples:** `PatientReference`, `Specimen`, `LabOrder`, `RequestedTest`,
  `ResultSet`, `Result`, `ResultFlag`, `ReferenceRange`, `QCResult`; patient
  name/ID/DOB, accession/specimen IDs, numeric and textual result values.
- **Handling / storage / encryption:** Encrypt in transit (mTLS / TLS).
  At-rest encryption expected on edge SQLite and any store that holds it
  (**OPEN:** at-rest specifics per environment). Deny-by-default access; access
  requires explicit role and recorded reason. Redacted from default UI views and
  all logs/metrics/bundles.
- **Retention:** Durable but bounded; **OPEN:** retention periods.
- **Redaction:** Default-on everywhere except authorized clinical views.
- **May live in:** Edge SQLite; control-plane PostgreSQL **only if** data
  governance approves (**OPEN:** whether cloud may ever hold clinical payloads).
- **Must not live in:** Logs, metrics labels, crash reports, screenshots,
  support bundles, R2, the repository, or CI.

### Class B — Raw device messages (High — treat as PHI)

- **Definition:** Untrusted bytes captured from analyzers. **May contain PHI**
  and are therefore handled at Class A protection level.
- **Examples:** `RawMessage` byte payloads from ASTM/HL7/delimited/file inputs;
  captured frames including unknown/unmapped content.
- **Handling / storage / encryption:** Persist-before-process into an
  append-oriented store with encryption support; treated as untrusted and as
  potential PHI. Same access, encryption, and redaction expectations as Class A.
- **Retention:** Configurable retention; **OPEN:** periods.
- **Redaction:** Redacted from default views/logs; raw view requires role +
  reason.
- **May live in:** Edge SQLite (encrypted, append-oriented).
- **Must not live in:** Metrics, logs, screenshots, support bundles unless
  de-identified and approved; never in repo/CI except as synthetic fixtures.

### Class C — Operational telemetry / metrics (Must be PHI-free)

- **Definition:** Health and performance signals about the system, containing no
  patient identifiers and no result values.
- **Examples:** Connection state, last byte/message timestamps, parser-error and
  checksum-failure counts, queue depth/age, delivery latency, retries, duplicates
  suppressed, dead-letter counts, certificate expiry, disk space, version drift,
  update status (§8).
- **Handling / storage / encryption:** Minimal collection by default; separated
  from clinical payloads. Metric **labels** carry only non-sensitive dimensions
  (e.g., gateway ID, device instance ID, driver version) — **never patient
  identifiers or result values.**
- **Retention:** Operational retention; **OPEN:** periods.
- **Redaction:** Not applicable if constructed PHI-free; enforced by test M11
  (see threat model).
- **May live in:** Metrics/telemetry systems, dashboards, operational logs.
- **Must not live in:** anything that would reintroduce PHI into a label or
  value.

### Class D — Audit / security events (High, integrity-critical)

- **Definition:** Tamper-evident records of configuration, mapping, driver,
  access, delivery, and security actions.
- **Examples:** `AuditEvent`, `SecurityEvent`; enrollment, support-access,
  approval, driver install/rollback, mapping change events.
- **Handling / storage / encryption:** Tamper-evident / append-oriented;
  "immutable enough for the agreed threat model" (§8). Encrypt in transit; at-rest
  per environment (**OPEN:**). Audit records themselves must not embed Class A/B
  values — reference identifiers, not PHI.
- **Retention:** Long-lived for accountability; **OPEN:** periods and legal-hold
  behavior.
- **Redaction:** Store references/provenance links, not raw PHI or secrets.
- **May live in:** Edge SQLite and control-plane PostgreSQL.
- **Must not live in:** Any surface as raw PHI/secret content.

### Class E — Secrets / keys (Critical)

- **Definition:** Material that grants access or establishes trust/integrity.
- **Examples:** Driver/release signing keys, device/gateway certificates and
  private keys, bootstrap tokens, rotated device credentials, OIDC tokens, R2
  service tokens, presigned URLs, database credentials.
- **Handling / storage / encryption:** Short-lived and automatically rotated
  where possible; least privilege. Held only in approved secret stores.
  **Never** in source, fixtures, screenshots, logs, metrics, crash reports, or
  diagnostic bundles (§3.1, §6). Personal browser cookies never shared with
  tools. **OPEN:** signing identities.
- **Retention:** Lifecycle-managed with rotation; revocation on compromise.
- **Redaction:** Never emitted; enforced by secret scanning (test M8).
- **May live in:** Dedicated secret stores; environment-scoped, per-environment.
- **Must not live in:** Repo, CI logs, PRs, R2 objects, support bundles, any
  emitted artifact.

### Class F — Tenant / configuration data (Moderate–High)

- **Definition:** Organizational, identity, and configuration data for the
  fleet.
- **Examples:** `Organization`, `Site`, `Laboratory`, `User`, `Role`,
  `Gateway`, `ConnectionProfile`, `TestMapping`/`UnitMapping`/`TerminologyMapping`,
  driver registry metadata, fleet inventory.
- **Handling / storage / encryption:** RBAC and tenant isolation enforced
  server-side; encrypt in transit; at-rest per environment (**OPEN:**). No PHI
  embedded.
- **Retention:** Lifecycle of the tenant relationship; **OPEN:** periods.
- **Redaction:** Personal user data minimized; not exposed cross-tenant.
- **May live in:** Control-plane PostgreSQL; relevant subset on edge SQLite.
- **Must not live in:** Cross-tenant scope; metrics labels beyond
  non-sensitive IDs.

### Class G — Driver packages / release artifacts (Integrity-critical, non-PHI)

- **Definition:** Signed, versioned, hash-verified driver packages and release
  assets. Data-first; must contain no secrets and no confidential vendor
  documents.
- **Examples:** Signed driver packages (manifest, declarative rules, golden and
  negative fixtures, simulator scenarios, digest, signature), SBOMs, checksums,
  release manifests, approved diagnostic bundles.
- **Handling / storage / encryption:** Signature + hash verified before use;
  encrypted, lifecycle-managed storage; presigned URLs, least-privilege service
  tokens, per-environment buckets, object versioning/retention, content checks
  (§9). Bundles must be redaction-checked before storage.
- **Retention:** Versioned with lifecycle policies; rollback/revocation
  supported.
- **Redaction:** Bundles carry no PHI/secrets; fixtures synthetic only.
- **May live in:** R2 (encrypted, per-env), driver registry.
- **Must not live in:** Any package/bundle carrying secrets or real PHI.

### Class H — Synthetic fixtures / test data (Low)

- **Definition:** Synthetic or irreversibly de-identified data used for
  development, conformance, and CI.
- **Examples:** Golden/negative ASTM/HL7 fixtures, simulator scenario files,
  expected canonical outputs and validation decisions.
- **Handling / storage / encryption:** No special confidentiality; still no
  secrets embedded. Each fixture carries source context, protocol/driver
  version, expected parse, expected normalized object, and expected
  validation/delivery decision (§5.2).
- **Retention:** Kept with the codebase.
- **Redaction:** Not required (already synthetic/de-identified).
- **May live in:** Repository, CI, R2 (synthetic fixtures bucket).
- **Must not live in:** n/a — but must never be real patient data. This is the
  **only** class of data permitted in the repository and CI.

---

## 3. Quick reference — where each class may live

| Class | Edge SQLite | PostgreSQL | R2 | Logs | Metric labels | Support bundle | Repo / CI |
|---|---|---|---|---|---|---|---|
| A — PHI / clinical | Yes (encrypted) | **OPEN** (gov. approval) | No | No | No | No (redacted) | No |
| B — Raw messages | Yes (encrypted) | No | No | No | No | No (redacted) | Synthetic only |
| C — Op. telemetry | Yes | Yes | No | Yes (PHI-free) | Yes (non-sensitive only) | Yes (PHI-free) | n/a |
| D — Audit / security | Yes | Yes | No | Refs only | No | Refs only | No |
| E — Secrets / keys | Secret store | Secret store | No | No | No | No | No |
| F — Tenant / config | Subset | Yes | No | Non-sensitive | Non-sensitive IDs only | Non-sensitive | No |
| G — Driver / release | Verified refs | Registry | Yes (encrypted) | Refs | No | Approved only | Synthetic parts only |
| H — Synthetic fixtures | For tests | For tests | Yes | Yes | Yes | Yes | Yes |

"Refs" = identifiers/provenance links only, never raw PHI or secret content.

---

## 4. Redaction and separation rules (summary)

- Redact patient fields and secrets from logs, metrics, crash reports,
  screenshots, and support bundles **by default** (§6).
- Keep patient/result payloads separated from operational telemetry at all times
  (§2.3).
- **Never** place patient identifiers or result values in metric labels or any
  telemetry (§8) — the single hardest rule in this document.
- Sensitive (PHI/secret) access requires explicit role and recorded reason (§5).
- The browser/UI never receives raw device credentials (§2.3).
- Diagnostic/support bundles are redacted and, for cloud storage, approved
  before leaving the gateway.

---

## 5. Open items

- **OPEN:** Deployment jurisdiction and applicable privacy law (GDPR / HIPAA /
  other) — not asserted here; drives retention, residency, and consent controls.
- **OPEN:** Retention periods for each class (raw, clinical, audit, telemetry,
  bundles) and legal-hold behavior.
- **OPEN:** Whether the cloud control plane (PostgreSQL / R2) may ever hold
  Class A clinical payloads.
- **OPEN:** At-rest encryption specifics per environment (edge SQLite,
  PostgreSQL, R2, backups).
- **OPEN:** Signing identities for driver packages and releases.
- **OPEN:** Security-owner contact and incident-response SLA.
- **OPEN:** Regulatory / healthcare certification is a separate legal/quality
  workstream (§6, §11) — this document neither asserts nor implies conformance to
  any standard.

This classification is a living Phase 0 draft, to be revisited alongside the
data-classification, retention, privacy, and incident-response policy work
called for in §4 Phase 0 step 6.
