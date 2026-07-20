# Cross-Platform Laboratory Analyzer Middleware — Detailed Development Plan

## 1. Product objective and boundaries

The product is a universal, adaptive connectivity platform that links laboratory analyzers to LIS/HIS systems through a consistent, validated interface. It is not a promise to interpret every unknown analyzer automatically. It provides zero/low configuration for certified profiles, guided configuration for recognizable protocols, and passive learning for unknown devices.

### 1.1 Primary outcomes

- Connect through RS-232, USB virtual COM, TCP client/server, HL7 MLLP, and watched files.
- Parse ASTM and HL7 v2, then add configurable delimited and fixed-width profiles.
- Normalize device messages into one laboratory data model.
- Exchange results, orders, queries, acknowledgements, device events, and later QC.
- Operate offline with durable store-and-forward behavior.
- Run on Windows and macOS, with Windows as the first certified production target.
- Manage gateways, drivers, mappings, updates, users, and audit centrally.
- Preserve raw evidence and a complete chain of custody for every transformation.

### 1.2 Explicit non-goals for the MVP

- No autonomous activation of inferred drivers.
- No unreviewed clinical mapping or automatic result release.
- No production patient data.
- No arbitrary probing or control commands to unidentified equipment.
- No native USB support beyond documented/approved drivers and virtual COM in the first release.
- No broad “all analyzer” certification claim.
- No Kubernetes unless measured scale demonstrates the need.

### 1.3 Support levels

1. **Certified:** exact model/firmware/workflow validated and signed.
2. **Compatible:** protocol family tested; site validation required.
3. **Experimental:** parses recorded samples only; cannot enter production.
4. **Capture-only:** unknown device; receive and retain bytes without clinical interpretation.

## 2. Architecture specification

### 2.1 Logical architecture

```text
Analyzer / Vendor Workstation
  → transport adapter (serial | TCP | MLLP | file)
  → session/framing state machine
  → protocol parser
  → device driver/profile
  → canonical laboratory model
  → validation + mapping + deduplication
  → durable outbox
  → LIS/HIS adapter (REST | HL7 | FHIR)

Technician UI ↔ local gateway API
Web control plane ↔ secure gateway management channel
Driver registry → signed package verification → staged gateway installation
```

### 2.2 Runtime components

**Edge service (`gatewayd`)**

- Rust daemon independent of the desktop UI.
- Windows Service and macOS launchd packaging.
- Port discovery, connection lifecycle, protocol runtime, drivers, local database, queue, API, telemetry, and update client.
- Continues processing when the UI, control plane, or internet is unavailable.

**Technician desktop app**

- Tauri 2, React, TypeScript.
- Local-only by default; connects to authenticated loopback API or Tauri IPC.
- Adds devices, captures messages, configures ports, maps fields, replays fixtures, runs validation, and exports diagnostic bundles.

**Control plane**

- React web application and ASP.NET Core modular backend.
- PostgreSQL, object storage, and a lightweight message broker when needed.
- Multi-tenant organization/lab model, gateway enrollment, configuration, driver registry, mapping approval, audit, and fleet status.

**Simulator and conformance toolkit**

- Rust or .NET command-line simulator with scenario files.
- Simulates ASTM link-layer states, HL7 MLLP, malformed frames, retries, duplicates, corrections, and network faults.

### 2.3 Trust boundaries

- Analyzer VLAN is untrusted input.
- Gateway-to-cloud uses mutually authenticated TLS or device certificates with rotation.
- Browser never receives raw device credentials.
- Driver packages are data-first, signed, versioned, and hash-verified.
- Complex transformations use a sandbox with strict CPU, memory, I/O, and time limits; no unrestricted scripts.
- Patient/result payloads are separated from operational telemetry.

### 2.4 Canonical data model

Required entities:

- Organization, Site, Laboratory, User, Role.
- Gateway, GatewayCertificate, DeviceInstance, ConnectionProfile.
- Driver, DriverVersion, CompatibilityRule, Signature.
- PatientReference, Specimen, LabOrder, RequestedTest.
- ResultSet, Result, ResultFlag, ReferenceRange, QCResult.
- TestMapping, UnitMapping, TerminologyMapping.
- RawMessage, ParsedMessage, TransformationRecord.
- DeliveryAttempt, Acknowledgement, DeadLetter.
- ValidationPlan, ValidationCase, ValidationRun, Approval.
- AuditEvent, SecurityEvent, SoftwareRelease.

Every result must retain identifiers linking it to the raw byte payload, parser/driver version, mapping version, validation decision, delivery attempt, and acknowledgement.

### 2.5 Driver package specification

Each driver package contains:

- Manifest: ID, vendor, models, firmware range, protocol family, transports, workflows, minimum runtime, status.
- Declarative session and framing rules.
- Record/field extraction rules.
- Mapping defaults and terminology hints.
- Golden fixtures and expected canonical outputs.
- Negative fixtures and expected failures.
- Simulator scenarios.
- Known limitations and site-validation checklist.
- Package digest and signature.

Driver state progresses: `draft → parsed → mapped → validated → certified → deprecated/revoked`.

## 3. Repository specification

```text
lab-connect/
├── .github/workflows/
├── apps/
│   ├── gateway-desktop/          # Tauri + React
│   ├── control-plane-web/        # React
│   └── docs-site/
├── services/
│   ├── control-plane-api/        # ASP.NET Core modular monolith
│   ├── gatewayd/                 # Rust service binary
│   └── simulator/                # analyzer simulator CLI/service
├── crates/
│   ├── canonical-model/
│   ├── transport-core/
│   ├── transport-serial/
│   ├── transport-tcp/
│   ├── transport-file/
│   ├── protocol-astm/
│   ├── protocol-hl7/
│   ├── protocol-delimited/
│   ├── driver-runtime/
│   ├── mapping-engine/
│   ├── durable-queue/
│   └── gateway-api/
├── packages/
│   ├── ui/
│   ├── api-client/
│   ├── contracts/
│   └── validation-schemas/
├── drivers/
│   ├── generic-astm/
│   ├── generic-hl7/
│   └── examples/
├── fixtures/                     # synthetic/de-identified only
├── tests/
│   ├── conformance/
│   ├── integration/
│   ├── security/
│   └── end-to-end/
├── deploy/
│   ├── railway/
│   ├── cloudflare/
│   ├── windows/
│   └── macos/
├── docs/
│   ├── adr/
│   ├── architecture/
│   ├── protocols/
│   ├── security/
│   ├── validation/
│   ├── operations/
│   └── product/
├── Cargo.toml
├── package.json
├── pnpm-workspace.yaml
├── Directory.Build.props
├── global.json
├── justfile
├── SECURITY.md
├── CONTRIBUTING.md
└── README.md
```

### 3.1 Engineering rules

- Trunk-based development with short feature branches and protected `main`.
- Conventional commits and automatically generated changelogs.
- Architecture decisions recorded in ADRs before material platform changes.
- Contracts versioned independently; backward compatibility tested.
- Synthetic data only in repository and CI.
- No protocol manual or confidential vendor document committed.
- Secrets never stored in source, fixtures, screenshots, logs, or diagnostic bundles.
- Dependency pinning, lockfiles, SBOM, license scanning, and provenance for releases.

## 4. Phased implementation plan

Estimates are planning ranges for a small experienced team and must be recalibrated after Phase 0. Do not treat them as commitments.

### Phase 0 — Governance, discovery, and architecture (2–4 weeks)

**Steps**

1. Name product owner, technical lead, clinical/laboratory safety owner, security owner, and release approver.
2. Interview laboratory staff, LIS/HIS integrators, biomedical engineers, and IT/network administrators.
3. Inventory first target analyzer: exact model, firmware, host interface license, port, cable, workflow, documentation revision, sample codes, and physical access.
4. Select the first three target profiles, but authorize implementation of only one initially.
5. Write product requirements, user journeys, support matrix, and explicit non-goals.
6. Create data classification, retention, privacy, and incident-response policies appropriate to deployment jurisdictions.
7. Threat-model analyzer, gateway, technician UI, update channel, control plane, support access, and LIS/HIS adapters.
8. Approve canonical model v0.1, REST contract v0.1, driver manifest v0.1, and validation lifecycle.
9. Create ADRs for Rust, Tauri, ASP.NET Core, PostgreSQL, Railway staging, Cloudflare/R2, and the modular-monolith start.
10. Define measurable MVP exit criteria and a change-control process.

**Deliverables**: PRD, architecture document, threat model, initial data model, API draft, driver schema, validation SOP, project risk register, prioritized backlog.

**Exit gate**: clinical and technical owners approve scope; one simulator-first vertical slice is fully specified; no unresolved decision blocks repository scaffolding.

### Phase 1 — Repository and developer foundation (1–2 weeks)

1. Create a private GitHub repository only after owner approval.
2. Add branch protection, required reviews, signed releases, issue/PR templates, CODEOWNERS, Dependabot/Renovate policy, and secret scanning.
3. Scaffold Rust workspace, pnpm workspace, .NET solution, Tauri app, and documentation directories.
4. Standardize formatting, linting, tests, local task commands, version files, and development containers where useful.
5. Add CI matrices for Rust, TypeScript, and .NET; build on Linux, Windows, and macOS where relevant.
6. Add artifact retention rules, test reports, coverage, SBOM generation, vulnerability and license scans.
7. Document local prerequisites and verify clean setup on one Windows and one macOS machine.

**Acceptance**: clean clone can build and test; CI is green; no secrets; empty applications start locally; architecture boundaries are enforced.

### Phase 2 — Canonical contracts and local persistence (2–3 weeks)

1. Implement strongly typed IDs, timestamps, decimal result handling, coded/text/numeric values, result status, flags, units, specimen, order, and provenance.
2. Define JSON Schema/OpenAPI contracts and generate clients/types where practical.
3. Implement SQLite schema and migrations for gateway configuration, messages, outbox, acknowledgements, deduplication keys, and audit.
4. Add append-oriented raw-message storage with encryption support and configurable retention.
5. Implement canonical validation rules without clinical guessing.
6. Add contract fixtures and round-trip/compatibility tests.

**Acceptance**: canonical objects round-trip without loss; exact decimals preserved; migrations upgrade/rollback in tests; every normalized result points to provenance.

### Phase 3 — Transport layer (3–5 weeks)

**Serial/USB virtual COM**

- Enumerate ports and stable device identifiers where the OS permits.
- Configure baud, parity, data/stop bits, flow control, encoding, timeouts.
- Support cancellation, reconnect, exclusive access, byte counters, and safe passive capture.
- Test USB removal/reinsert, sleep/wake, permission failures, locked ports, and partial reads on both OS targets.

**TCP**

- Client and server modes, bind controls, allowlists, TLS where applicable, keepalive, backoff with jitter, idle/session timeouts.
- Protect against connection floods, oversized messages, and unbounded buffers.

**File watcher**

- Atomic file detection, stable-write delay, idempotency, quarantine, archival, encoding detection, and path allowlists.

**Acceptance**: deterministic reconnect; no message loss in tested fault cases; bounded resource use; diagnostic state visible; capture-only mode cannot transmit.

### Phase 4 — ASTM protocol engine and simulator (4–6 weeks)

1. Implement ENQ/ACK/NAK/EOT link-layer state machine as a separately tested module.
2. Implement STX, frame number, ETB/ETX, checksum, CR/LF framing, multi-frame assembly, and sequence validation.
3. Add configurable contention, retry, timeout, maximum-frame/message limits.
4. Parse H/P/O/R/C/Q/L records into a lossless intermediate representation.
5. Preserve unknown fields/components and raw records.
6. Build simulator scenarios: normal, multi-frame, malformed checksum, lost ACK, NAK/retry, duplicate, timeout, disconnect, host query.
7. Use property/fuzz tests for parsers and deterministic state-machine tests with a virtual clock.

**Acceptance**: golden vectors pass; malformed input never panics; link state is reproducible; fault injection demonstrates retry and recovery; simulator supports CI.

### Phase 5 — Gateway vertical slice and technician UI (4–6 weeks)

1. Implement gateway lifecycle, configuration, per-device isolation, health state, structured logs, metrics, and local authenticated API.
2. Implement the processing pipeline: receive → persist raw → parse → normalize → validate → map → deduplicate → queue → deliver/hold.
3. Build UI flows: first-run security, port discovery, add device, capture-only mode, raw/parsed views, mapping review, replay, validation, diagnostics export.
4. Redact PHI and secrets from default views/logs; require explicit role and reason for sensitive access.
5. Add simulator onboarding wizard and a clear status lifecycle.
6. Package unsigned local development builds for Windows/macOS; do not distribute as production installers yet.

**Acceptance**: complete synthetic ASTM result traverses the system and survives restart/network loss; duplicate does not create duplicate delivery; audit shows each step.

### Phase 6 — HL7 v2 and LIS/HIS adapters (4–6 weeks)

1. Implement MLLP listener/client with limits, ACK modes, retries, and connection controls.
2. Parse/generate required subsets of ORU, OML/ORM, ACK, OBR, OBX, SPM, and SAC; document supported versions and profiles.
3. Implement stable REST endpoints for results, orders, acknowledgements, devices, and gateway health.
4. Add idempotency keys, correlation IDs, delivery receipts, retry policies, dead-letter review, and reconciliation.
5. Create a mock LIS/HIS service and end-to-end contract suite.
6. Defer broad FHIR implementation; specify mapping and add the minimum resources only when a real integration requires them.

**Acceptance**: bidirectional synthetic workflow passes; ACK/NAK and downtime behavior is deterministic; contract compatibility tests run in CI.

### Phase 7 — Driver runtime and low-code profiles (4–7 weeks)

1. Validate manifests with JSON Schema and semantic rules.
2. Implement inheritance, firmware compatibility, capabilities, configuration defaults, and migration between driver versions.
3. Implement declarative field extraction, transformations, mappings, and validation constraints.
4. Sandbox any advanced transformation mechanism; default to no executable code.
5. Add signing, verification, revocation, rollback, and installation audit.
6. Generate conformance tests and simulator scenarios from each driver package.
7. Build a profile editor with diff, review, approval, and promotion states.

**Acceptance**: a generic ASTM driver is installed, verified, upgraded, rolled back, and rejected when tampered with; profile changes are reviewable and reproducible.

### Phase 8 — Cloud control plane (5–8 weeks)

1. Implement tenant/site/lab/user/role model and OIDC authentication.
2. Build gateway enrollment using short-lived bootstrap tokens and rotated device credentials.
3. Add device inventory, fleet health, configuration versions, driver registry, mapping approvals, audit, and update channels.
4. Separate operational telemetry from clinical payloads and default to minimal data collection.
5. Add rate limits, tenant isolation tests, backup/restore, database migration policy, and support-access audit.
6. Define a secure outbound gateway connection so no inbound port is required at most sites.

**Acceptance**: tenant isolation tests pass; a gateway enrolls and receives signed non-production configuration; backup restore is demonstrated; audit is immutable enough for the agreed threat model.

### Phase 9 — GitHub, Railway, Cloudflare/R2 staging (2–4 weeks)

**GitHub**

- Use private repository, environments (`development`, `staging`, later `production`), protected secrets, OIDC where available, required approvals, release provenance.
- Browser login does not equal permission to create repositories or change settings; Claude must request explicit confirmation before external writes.

**Railway**

- Suitable for development/staging control-plane API, web app, PostgreSQL, and worker.
- Define services as code, health checks, migrations as controlled jobs, resource/budget alerts, private networking, backups, and separate environments.
- Do not place the edge gateway on Railway; it runs onsite.
- Production suitability must be reviewed for data residency, contractual, security, availability, and healthcare compliance needs.

**Cloudflare/R2**

- Use R2 for encrypted, lifecycle-managed artifacts such as signed driver packages, synthetic fixtures, release assets, and approved diagnostic bundles.
- Use presigned URLs, least-privilege service tokens, CORS restrictions, object versioning/retention as required, malware/content checks, and separate buckets per environment.
- Do not store identifiable clinical data in R2 until data governance explicitly approves it.
- DNS, custom domains, WAF, and access policies require an explicit change window.

**Acceptance**: staging deploys from an approved commit; rollback is tested; no production domain/account is mutated; secrets are not exposed to pull requests or logs.

### Phase 10 — First physical analyzer integration (4–8+ weeks)

1. Obtain legal access to the correct host-interface documentation and confirm interface licensing.
2. Establish isolated test network and passive capture first.
3. Record controlled cases with known expected outputs; de-identify exported fixtures.
4. Implement exact device profile and firmware compatibility.
5. Validate normal, abnormal, critical, text, rerun, dilution, correction, QC, unknown barcode, timeout, duplicate, and restart cases applicable to the device.
6. Run side-by-side comparison against existing validated workflow; require laboratory sign-off.
7. Enable outbound orders only after inbound results are fully validated and a separate approval is recorded.

**Acceptance**: signed validation report; zero unexplained discrepancies; rollback and downtime SOPs rehearsed; profile remains site-limited until wider evidence exists.

### Phase 11 — Security hardening and release engineering (3–6 weeks, then continuous)

- Independent threat-model review, dependency audit, static/dynamic testing, fuzzing, penetration test, installer/update review, and secrets review.
- Windows code signing and macOS Developer ID/notarization; secure auto-update with signed metadata and staged channels.
- Least-privilege service accounts, file permissions, local API authentication, certificate rotation, encrypted database/backup approach, log redaction.
- Reproducible release manifest, SBOM, provenance, checksums, release notes, rollback package, and support matrix.
- Disaster recovery, incident response, vulnerability disclosure, support-access, and certificate-compromise exercises.

**Acceptance**: critical/high findings resolved or formally risk-accepted; installers verify; update/rollback succeeds; recovery objectives are demonstrated.

### Phase 12 — Staged production rollout (6–12+ weeks)

1. Internal dogfood with simulator and synthetic data.
2. Lab bench validation on one analyzer without LIS writeback.
3. Shadow mode: receive/compare, do not release results.
4. Controlled unidirectional production pilot with manual reconciliation.
5. Expand to bidirectional only after separate validation.
6. Add a small cohort of sites/models with canary gateway and driver releases.
7. Scale only after reliability, discrepancy, support, and recovery metrics meet thresholds.

**Go/no-go metrics**: message loss 0 in validated paths; unexplained clinical discrepancies 0; duplicate release 0; queue recovery verified; acknowledgement reconciliation complete; operational availability target met; support and rollback readiness confirmed.

## 5. Testing strategy

### 5.1 Test pyramid

- Unit: framing, checksums, parsing, mappings, deduplication, state transitions.
- Property/fuzz: untrusted byte parsers, length boundaries, malformed encodings.
- Contract: canonical schemas, REST, HL7 profiles, backward compatibility.
- Integration: serial loopback, TCP, file watcher, SQLite, API/database/object storage.
- End-to-end: analyzer simulator through gateway to mock LIS and control plane.
- Fault injection: dropped ACK, corrupted frame, disk full, clock shift, restart, network partition, certificate expiration.
- Platform: Windows and macOS port naming, permissions, service lifecycle, suspend/resume, installer/update.
- Security: authz, tenant isolation, SSRF/path traversal, package tampering, secret leakage, resource exhaustion.
- Clinical validation: expected/actual comparison signed by authorized laboratory staff.

### 5.2 Required fixtures

All repository fixtures are synthetic or irreversibly de-identified. Each has source context, protocol/driver version, expected parse, expected normalized object, and expected validation/delivery decision.

### 5.3 Quality gates

- Formatting/lint/type checks.
- Unit/integration/conformance tests.
- Parser fuzz smoke tests on every PR; longer campaigns scheduled.
- Dependency, license, secret, and code scans.
- Coverage thresholds focused on safety-critical modules, not a single vanity percentage.
- Windows/macOS packaging smoke tests before release.
- Manual approval for staging/production deployments and signed driver promotion.

## 6. Security and privacy baseline

- Apply least privilege and deny-by-default network/file/device access.
- Encrypt in transit; define at-rest controls per environment and threat model.
- Use short-lived credentials and automated rotation; never share personal browser cookies with tools.
- Redact patient fields from logs, metrics, crash reports, screenshots, and support bundles.
- Maintain tamper-evident audit records for configuration, mapping, driver, access, and delivery events.
- Validate all lengths, encodings, paths, archives, and driver inputs; use bounded queues and backpressure.
- Separate roles: technician, mapping reviewer, clinical approver, operator, security administrator, auditor.
- Require dual approval for certified driver publication and production mapping changes.
- Document retention/deletion, export, legal hold, breach response, and customer isolation.
- Treat healthcare regulatory certification as a dedicated legal/quality workstream, not a software checkbox.

## 7. Environment and deployment strategy

### Local development

- macOS and Windows developer machines; Rust stable pinned, Node LTS pinned, pnpm pinned, .NET SDK pinned.
- SQLite and simulator for the default path; containers for PostgreSQL and optional broker.
- `.env.example` contains names and safe defaults only. Real values live in approved secret stores.

### Development cloud

- Ephemeral/low-cost Railway services with synthetic data.
- Disposable database and R2 development bucket.
- Automatic deploys allowed only from the development branch/environment after CI.

### Staging

- Production-like topology, isolated accounts/projects/buckets, synthetic/de-identified validation data, manual promotion.
- Backups, restore tests, monitoring, alerting, and rollback exercised.

### Production

- Created only after compliance/hosting review and explicit authorization.
- Separate credentials, domains, database, R2 bucket, signing keys, monitoring, support access, budgets, and on-call process.
- Edge releases use stable/canary channels and remain functional offline.

## 8. Operations and observability

Track connection state, last byte/message, parser errors, checksum failures, queue depth/age, delivery latency, retries, duplicates suppressed, dead letters, certificate expiry, disk space, version drift, and update status. Never put result values or patient identifiers in metrics labels.

Define runbooks for analyzer offline, port locked, checksum storm, mapping unresolved, LIS unavailable, queue growth, disk pressure, expired certificate, failed update, suspected compromise, lost gateway, and driver rollback.

## 9. Team and execution model

Minimum accountable roles: product owner, Rust/edge engineer, frontend/Tauri engineer, backend/platform engineer, QA/automation engineer, DevSecOps support, laboratory domain expert, and clinical validation approver. One person may cover several engineering roles early, but clinical approval and production authorization must remain distinct.

Use two-week iterations with a demonstrable vertical slice, risk review, updated ADRs, test evidence, and backlog recalibration. Claude Code may implement scoped issues, but humans own clinical semantics, external-account authorization, and production approval.

## 10. Definition of MVP complete

- Signed Windows development/pilot installer and notarized macOS test installer strategy demonstrated.
- Gateway service survives UI closure, restart, and internet outage.
- Serial/TCP/file transports and ASTM engine pass conformance/fault tests.
- Synthetic result traverses to mock LIS with complete audit/provenance and no duplicate delivery.
- Generic ASTM driver lifecycle, signing verification, rollback, and simulator work.
- Technician can configure, capture, map, validate, and export a redacted diagnostic bundle.
- Staging control plane manages one gateway without exposing PHI.
- Security review, restore, update rollback, incident and downtime runbooks have evidence.
- One physical analyzer profile has completed controlled validation before any production claim.

## 11. Principal risks and mitigations

- **Unavailable/proprietary manuals:** begin with simulator and legally obtained documentation; never reverse-engineer beyond authorization.
- **Clinical semantic error:** no inferred mapping auto-activation; dual review and golden cases.
- **OS/vendor incompatibility:** certify per analyzer/firmware/OS; Windows-first production.
- **Message loss/duplication:** persist-before-process, idempotency, durable outbox, reconciliation.
- **Unsafe device command:** passive default, capability allowlist, separate outbound approval.
- **Scope explosion:** one vertical slice and one physical analyzer before dynamic discovery/AI.
- **Cloud compliance mismatch:** staging only until legal/security/data-residency review.
- **Agent overreach:** phase gates, explicit external-write approvals, PR-based changes, no production credentials.
