# Product Requirements Document — Laboratory Analyzer Middleware ("lab-connect")

**Status:** Draft (Phase 0)
**Date:** 2026-07-18
**Note:** Items marked **OPEN:** are unresolved decisions owned by humans, not the engineering team. No **OPEN:** item blocks Phase 1 repository scaffolding or the simulator-only ASTM vertical slice; each must, however, be resolved before the specific later phase it gates (named inline). This PRD is derived from and must not contradict `DEVELOPMENT_PLAN.md`, `README.md`, and `OPERATOR_CHECKLIST.md`, which remain the source of truth.

---

## 1. Purpose and scope of this document

This PRD defines *what* the laboratory analyzer middleware must do, for *whom*, and the boundaries within which it operates. It does not define implementation detail (see the architecture document) or the phased schedule (see `DEVELOPMENT_PLAN.md`). It exists to let clinical and technical owners approve scope before any code is written, and to make every unresolved decision explicit rather than buried as a silent assumption.

Working repository/product name is **lab-connect** throughout this document. **OPEN:** final product name, repository name, and repository owner/organization are not yet decided (`OPERATOR_CHECKLIST.md` requires the owner to choose these). Gates Phase 1 repository creation, not local scaffolding.

### 1.1 Regulatory posture (read first)

This product is planning-stage software. **No regulatory or compliance status is claimed.** This document does not assert FDA clearance, CE marking, IVDR conformity, HIPAA compliance, GDPR compliance, ISO 13485/15189 certification, or any other regulatory position. Parsing a message, normalizing it, or passing it through a simulator establishes **no clinical validity whatsoever**. Clinical validity is established only by the controlled, signed laboratory validation described in Section 9 and in `DEVELOPMENT_PLAN.md` Phase 10.

**OPEN:** regulatory/quality framework (e.g. which jurisdiction's medical-device and laboratory-data regime applies) and the named, accountable regulatory/quality advisor. Regulatory certification is a **separate legal and quality workstream**, not a software checkbox, and is out of scope for engineering to assert or self-certify. Gates any production claim (Phase 10+).

### 1.2 Data-handling posture (read first)

Only synthetic or irreversibly de-identified data is permitted anywhere in this project — in the repository, in CI, in fixtures, in screenshots, in logs, and in diagnostic bundles. **No real patient data** is authorized at any point covered by this PRD.

**OPEN:** deployment jurisdiction(s) and data-residency constraints. Gates Phase 8 (cloud control plane) and any production hosting decision.
**OPEN:** data retention and deletion policy, and whether the cloud tier may *ever* hold clinical payloads (default: it may not, until data governance explicitly approves). Gates Phase 8 and Phase 9 (staging/R2 storage of any clinical data).

---

## 2. Product objective and boundaries

lab-connect is a universal, adaptive connectivity platform that links laboratory analyzers to Laboratory Information Systems (LIS) and Hospital Information Systems (HIS) through a consistent, validated interface. It provides a spectrum of support: zero/low configuration for certified device profiles, guided configuration for recognizable protocol families, and passive learning for unknown devices.

The product is explicitly **not** a promise to automatically interpret every unknown analyzer, and never auto-activates an inferred driver. Until a device profile is reviewed and validated, the gateway operates in **passive capture mode** only.

### 2.1 In scope

| Area | In scope for the product |
|---|---|
| Transports | RS-232 serial, USB virtual COM (VCP), TCP client/server, HL7 MLLP, watched files |
| Protocols | ASTM and HL7 v2 first; configurable delimited and fixed-width profiles later |
| Data | Normalize device messages into one canonical laboratory data model |
| Exchange | Results, orders, queries, acknowledgements, device events; QC later |
| Operation | Offline-capable with durable store-and-forward |
| Platforms | Windows (first certified production target) and macOS |
| Management | Central management of gateways, drivers, mappings, updates, users, and audit |
| Evidence | Preserve raw bytes and a complete chain of custody for every transformation |

### 2.2 Out of scope / boundaries

The product does not perform clinical interpretation, does not release results without human-approved mapping and validation, does not issue control or probing commands to unidentified equipment, and does not make broad "all analyzer" certification claims. Section 3 lists MVP non-goals precisely.

### 2.3 Primary outcomes

1. Connect through RS-232, USB virtual COM, TCP client/server, HL7 MLLP, and watched files.
2. Parse ASTM and HL7 v2, then add configurable delimited and fixed-width profiles.
3. Normalize device messages into one laboratory data model without lossy guessing.
4. Exchange results, orders, queries, acknowledgements, device events, and later QC.
5. Operate offline with durable store-and-forward behavior and no message loss on tested fault paths.
6. Run on Windows and macOS, with Windows as the first certified production target.
7. Manage gateways, drivers, mappings, updates, users, and audit centrally.
8. Preserve raw evidence and a complete chain of custody for every transformation.

---

## 3. Explicit non-goals for the MVP

These are deliberate exclusions from the MVP. They are commitments, not oversights.

- **No autonomous activation of inferred drivers.** A driver is never promoted to production automatically.
- **No unreviewed clinical mapping and no automatic result release.** Every mapping is human-reviewed; every clinical release is human-approved.
- **No production patient data.** Synthetic/de-identified only.
- **No arbitrary probing or control commands to unidentified equipment.** Passive by default.
- **No native USB support beyond documented/approved drivers and virtual COM** in the first release.
- **No broad "all analyzer" certification claim.**
- **No Kubernetes** unless measured scale later demonstrates the need (start as a modular monolith).
- **No broad FHIR implementation** in the MVP; specify mapping and add minimum resources only when a real integration requires them.
- **No cloud storage of identifiable clinical data** until data governance explicitly approves (see Section 1.2 OPEN items).

---

## 4. Support levels

Every device operates at exactly one support level. The level is a safety control, not a marketing tier: it governs whether a device may reach production.

| Level | Meaning | May enter production? | Evidence required |
|---|---|---|---|
| **Certified** | Exact model + firmware + workflow validated and cryptographically signed | Yes | Signed validation report; certified, signed driver package |
| **Compatible** | Protocol family tested; not this exact model/firmware | Yes, only after site validation | Protocol conformance + per-site validation sign-off |
| **Experimental** | Parses recorded synthetic samples only | No | Recorded-sample parse evidence; explicitly blocked from production |
| **Capture-only** | Unknown device; receive and retain raw bytes with no clinical interpretation | No (capture-only cannot transmit) | Raw capture retained; no normalization asserted as clinical |

Rules:
- Experimental and Capture-only devices **cannot** release results to a LIS/HIS.
- Capture-only mode is transmit-incapable by design; the UI and gateway must make the current level unambiguous at all times.
- Promotion between levels follows the driver lifecycle (`draft → parsed → mapped → validated → certified → deprecated/revoked`) and requires the reviews in Section 8.

---

## 5. Target users and user journeys

Six primary roles, each with a distinct responsibility and separation of duties. No single person may both author a clinical mapping and approve its production release; clinical approval and production authorization are always distinct from engineering.

| Role | Primary responsibility | Key surface |
|---|---|---|
| Technician | Physically connect analyzers, capture traffic, configure ports, run onboarding | Tauri desktop app (local) |
| Mapping reviewer | Review field/unit/terminology mappings for correctness | Desktop + control-plane review UI |
| Clinical approver | Approve that a mapping/validation is clinically safe to release | Control-plane approval UI |
| Operator | Run day-to-day fleet operations, watch health, act on runbooks | Control-plane fleet dashboard |
| Security administrator | Manage users, roles, certificates, signing, access policy | Control-plane security/admin UI |
| Auditor | Read-only review of the tamper-evident audit trail and provenance | Control-plane audit views |

### 5.1 Technician journey

1. Launches the desktop app; completes first-run security (local authenticated API / Tauri IPC, loopback only by default).
2. Discovers serial/USB ports or configures a TCP/file source.
3. Adds a device; the device starts in **Capture-only** unless a certified profile is selected.
4. Captures raw messages from the analyzer (or from the simulator) and views raw + parsed side by side.
5. Replays recorded fixtures, runs validation cases, and exports a **redacted** diagnostic bundle (PHI and secrets removed by default).
6. Never releases results directly; hands mappings to the mapping reviewer.

### 5.2 Mapping reviewer journey

1. Opens a proposed mapping (test, unit, terminology) with a diff against the prior version.
2. Inspects the provenance chain: raw bytes → parsed → canonical → proposed mapping.
3. Requests changes or marks the mapping ready; cannot self-approve for clinical release.

### 5.3 Clinical approver journey

1. Reviews the reviewed mapping plus validation evidence (expected vs actual on synthetic/controlled cases).
2. Approves clinical safety, or rejects with reason. Dual approval is required for certified driver publication and production mapping changes.
3. Approval is recorded as an immutable audit event.

### 5.4 Operator journey

1. Monitors fleet health: connection state, queue depth/age, delivery latency, retries, dead letters, certificate expiry, disk, version drift.
2. Responds to alerts using runbooks (analyzer offline, port locked, checksum storm, LIS unavailable, queue growth, disk pressure, expired certificate, failed update, lost gateway, driver rollback).
3. Cannot alter clinical mappings; escalates to mapping reviewer/clinical approver.

### 5.5 Security administrator journey

1. Manages tenants, users, roles, and least-privilege access.
2. Manages gateway enrollment (short-lived bootstrap tokens, rotated device credentials), certificate rotation, and driver signing/revocation.
3. Manages code-signing identities and support-access audit.

### 5.6 Auditor journey

1. Reads the tamper-evident audit and provenance records for configuration, mapping, driver, access, and delivery events.
2. Confirms every released result links back to raw bytes, parser/driver version, mapping version, validation decision, delivery attempt, and acknowledgement.
3. Has no mutation rights.

---

## 6. Functional requirements

Requirement IDs use `FR-<area>-<n>`. "MUST" is mandatory for the MVP unless marked *(later)*.

### 6.1 Transports

| ID | Requirement |
|---|---|
| FR-T-1 | MUST support RS-232 serial and USB virtual COM: enumerate ports and stable device identifiers where the OS permits; configure baud, parity, data/stop bits, flow control, encoding, timeouts. |
| FR-T-2 | Serial MUST support cancellation, reconnect, exclusive access, byte counters, and safe passive capture; MUST survive USB removal/reinsert, sleep/wake, permission failures, locked ports, and partial reads on both OS targets. |
| FR-T-3 | MUST support TCP client and server modes with bind controls, allowlists, TLS where applicable, keepalive, backoff with jitter, and idle/session timeouts. |
| FR-T-4 | TCP MUST be protected against connection floods, oversized messages, and unbounded buffers. |
| FR-T-5 | MUST support HL7 MLLP listener and client with framing limits, ACK modes, retries, and connection controls. |
| FR-T-6 | MUST support a file watcher with atomic-file detection, stable-write delay, idempotency, quarantine, archival, encoding detection, and path allowlists. |
| FR-T-7 | Capture-only transports MUST be incapable of transmitting to the analyzer. |

**OPEN:** first analyzer's exact host interface (transport, cable, port settings) and licensed interface documentation. Gates Phase 10 (physical analyzer). Does not gate simulator work.

### 6.2 Protocols

| ID | Requirement |
|---|---|
| FR-P-1 | MUST implement an ASTM link-layer state machine (ENQ/ACK/NAK/EOT) as a separately tested module. |
| FR-P-2 | MUST implement ASTM framing: STX, frame number, ETB/ETX, checksum, CR/LF, multi-frame assembly, sequence validation; configurable contention, retry, timeout, and max-frame/message limits. |
| FR-P-3 | MUST parse ASTM H/P/O/R/C/Q/L records into a lossless intermediate representation, preserving unknown fields/components and raw records. |
| FR-P-4 | MUST parse/generate required subsets of HL7 v2 (ORU, OML/ORM, ACK, OBR, OBX, SPM, SAC) and document supported versions and profiles. |
| FR-P-5 | Malformed input MUST never panic or crash the runtime; parsers are fuzz/property tested. |
| FR-P-6 *(later)* | Configurable delimited and fixed-width profiles. |
| FR-P-7 *(later)* | QC result exchange. |

**OPEN:** target LIS/HIS protocol/profile (e.g. specific HL7 v2 version and profile, or REST/FHIR shape) and sample contracts. Gates Phase 6 (LIS/HIS adapters). A generic HL7 v2 subset and mock LIS are used until this is resolved.

### 6.3 Canonical normalization

| ID | Requirement |
|---|---|
| FR-C-1 | MUST normalize parsed messages into one canonical laboratory model (Organization, Site, Laboratory, Gateway, DeviceInstance, PatientReference, Specimen, LabOrder, RequestedTest, ResultSet, Result, ResultFlag, ReferenceRange, mappings, RawMessage, ParsedMessage, TransformationRecord, delivery/audit entities). |
| FR-C-2 | MUST preserve exact decimal result values with no float rounding (see NFR-4). |
| FR-C-3 | MUST NOT perform clinical guessing during normalization; unknown fields are preserved, not inferred. |
| FR-C-4 | Every normalized result MUST link to raw bytes, parser/driver version, mapping version, validation decision, delivery attempt, and acknowledgement. |
| FR-C-5 | Canonical objects MUST round-trip without loss (validated by contract fixtures and round-trip tests). |

### 6.4 Store-and-forward

| ID | Requirement |
|---|---|
| FR-S-1 | MUST persist raw messages before processing (persist-before-process). |
| FR-S-2 | MUST use a durable outbox with idempotency keys, deduplication, retry policy, dead-letter review, and reconciliation. |
| FR-S-3 | A duplicate input MUST NOT create a duplicate delivery. |
| FR-S-4 | MUST survive UI closure, service restart, and internet outage without losing queued messages. |
| FR-S-5 | MUST provide append-oriented raw-message storage with encryption support and configurable retention. |

### 6.5 Central management

| ID | Requirement |
|---|---|
| FR-M-1 | MUST provide a multi-tenant organization/site/lab/user/role model with OIDC authentication. |
| FR-M-2 | MUST support gateway enrollment via short-lived bootstrap tokens and rotated device credentials, using a secure outbound connection so most sites need no inbound port. |
| FR-M-3 | MUST provide device inventory, fleet health, configuration versions, a signed driver registry, mapping approvals, immutable audit, and staged update channels. |
| FR-M-4 | MUST separate operational telemetry from clinical payloads and default to minimal data collection. |
| FR-M-5 | Driver packages MUST be data-first, signed, versioned, and hash-verified, with revocation and rollback. |
| FR-M-6 | The browser MUST never receive raw device credentials or PHI beyond least-privilege need, with explicit role + reason for sensitive access. |

---

## 7. Non-functional requirements

### 7.1 Safety
- The system defaults to passive capture; it never sends control commands to an unidentified device.
- No clinical validity is derived from parsing or simulation alone.
- Experimental/Capture-only devices are structurally prevented from releasing results.

### 7.2 Reliability / no message loss
- Zero message loss on all tested fault paths (dropped ACK, corrupted frame, disk full, clock shift, restart, network partition, certificate expiration).
- Deterministic reconnect and deterministic ACK/NAK/downtime behavior.
- Persist-before-process, idempotent delivery, durable outbox, dead-letter handling, and acknowledgement reconciliation.

### 7.3 Security
- Least privilege and deny-by-default network/file/device access; bounded queues and backpressure.
- Encrypt in transit; at-rest controls defined per environment and threat model.
- Short-lived credentials with automated rotation; never share personal browser cookies/sessions with tools.
- Redact patient fields and secrets from logs, metrics, crash reports, screenshots, and support bundles.
- Validate all lengths, encodings, paths, archives, and driver inputs (SSRF, path traversal, package tampering, resource exhaustion defenses).
- Complex transformations run in a sandbox with strict CPU/memory/I/O/time limits; no unrestricted scripts (default: no executable code in drivers).

**OPEN:** Windows and macOS code-signing identities (Authenticode / Apple Developer ID + notarization). Gates Phase 11 (release engineering) and any signed installer distribution. Not needed for unsigned local dev builds.

### 7.4 Bounded resources
- All buffers, frame sizes, message sizes, and queues are bounded; oversized inputs are rejected safely.
- Memory, CPU, and disk usage stay bounded under sustained load and fault injection.

### 7.5 Exact decimal handling
- Numeric results are stored and transported as exact decimals; no floating-point representation of result magnitudes. Round-trip tests must prove exact preservation.

### 7.6 Provenance / audit
- Tamper-evident audit records for configuration, mapping, driver, access, and delivery events.
- Every released result is traceable end-to-end (raw → parsed → canonical → mapping → validation → delivery → acknowledgement).
- Audit must be immutable enough for the agreed threat model.

### 7.7 Offline operation
- The edge service (`gatewayd`) continues receiving, persisting, parsing, normalizing, and queuing when the UI, control plane, or internet is unavailable, forwarding when connectivity returns.

### 7.8 Cross-platform, Windows-first
- Runs on Windows and macOS. Windows is the first certified production target; macOS compatibility is certified per analyzer because vendor drivers/middleware may be Windows-only.
- Packaged as a Windows Service and macOS launchd daemon, independent of the desktop UI.

### 7.9 Availability / recovery
**OPEN:** production availability and recovery objectives (RTO/RPO, uptime target). Gates Phase 11/Phase 12 go/no-go. For MVP, the measurable target is functional correctness and demonstrated recovery, not a numeric SLA.

---

## 8. Governance and separation of duties

- Distinct roles: technician, mapping reviewer, clinical approver, operator, security administrator, auditor.
- **Dual approval** is required for certified driver publication and for production mapping changes.
- Clinical semantics, external-account authorization, and production approval are owned by humans; automated agents may implement scoped engineering issues only, through reviewed PRs, with no production credentials.
- Driver state lifecycle: `draft → parsed → mapped → validated → certified → deprecated/revoked`.

**OPEN:** named product owner, technical lead, clinical/laboratory safety owner, security owner, and release approver (Phase 0 step 1). Gates the Phase 0 exit gate.

---

## 9. Fixed technology stack

This stack is fixed. Alternatives are out of scope for this PRD.

| Layer | Technology |
|---|---|
| Edge service (`gatewayd`) | Rust, pinned to 1.97.1 |
| Technician desktop app | Tauri 2 + React + TypeScript |
| Control-plane backend | ASP.NET Core on .NET 10 LTS (modular monolith) |
| Control-plane web app | React |
| Edge persistence | SQLite |
| Central persistence | PostgreSQL |
| Repository | Monorepo (`lab-connect/`) |

Cloud staging targets (per `DEVELOPMENT_PLAN.md`) are Railway (control-plane API/web/PostgreSQL/worker; never the edge gateway) and Cloudflare/R2 (encrypted, lifecycle-managed artifacts only). Production hosting is created only after compliance/hosting review — see Section 1.2 OPEN items.

---

## 10. Recommended first milestone — simulator ASTM vertical slice

The first milestone proves one complete path end-to-end using a **simulator only**, with no physical analyzer and no real data:

```
simulated ASTM analyzer → Rust gateway (gatewayd) → normalized canonical result
  → local authenticated API → technician UI → complete audit trail
```

Scope:
- ASTM link-layer and framing engine with a deterministic, virtual-clock state machine.
- Simulator scenarios: normal, multi-frame, malformed checksum, lost ACK, NAK/retry, duplicate, timeout, disconnect, host query.
- Persist-before-process pipeline: receive → persist raw → parse → normalize → validate → map → deduplicate → queue → deliver/hold.
- Technician UI flows: first-run security, port discovery, add device, capture-only mode, raw/parsed views, mapping review, replay, validation, redacted diagnostic export.
- Unsigned local dev builds only (no production installers).

This milestone establishes engineering plumbing and provenance. It establishes **no clinical validity**; a real analyzer must be validated separately (Phase 10) before any production claim.

---

## 11. Measurable MVP exit criteria

The MVP is complete when all of the following have evidence:

| # | Criterion | Measure |
|---|---|---|
| 1 | Signed Windows dev/pilot installer and notarized macOS test installer strategy demonstrated | Reproducible signed artifacts (gated by Section 7.3 OPEN signing identities) |
| 2 | Gateway service survives UI closure, restart, and internet outage | Fault-injection test evidence; no message loss |
| 3 | Serial/TCP/file transports and ASTM engine pass conformance/fault tests | Green conformance + fault suites in CI |
| 4 | Synthetic result traverses to a mock LIS with complete audit/provenance and no duplicate delivery | End-to-end run with full provenance chain; duplicate-suppression proven |
| 5 | Generic ASTM driver lifecycle: install, signing verification, upgrade, rollback, tamper-rejection, simulator | Driver lifecycle test evidence |
| 6 | Technician can configure, capture, map, validate, and export a redacted diagnostic bundle | UI walkthrough evidence; bundle proven PHI/secret-free |
| 7 | Staging control plane manages one gateway without exposing PHI | Gateway enrolls, receives signed non-production config; tenant isolation tests pass |
| 8 | Security review, restore, update rollback, incident and downtime runbooks have evidence | Documented exercises with results |
| 9 | One physical analyzer profile has completed controlled validation before any production claim | Signed validation report; zero unexplained discrepancies (gated by Section 6.1 OPEN analyzer identity) |

Cross-cutting go/no-go metrics (from `DEVELOPMENT_PLAN.md`): message loss = 0 in validated paths; unexplained clinical discrepancies = 0; duplicate release = 0; queue recovery verified; acknowledgement reconciliation complete; operational availability target met (see Section 7.9 OPEN); support and rollback readiness confirmed.

---

## 12. Consolidated OPEN items register

| # | OPEN item | Owner (human) | Gates |
|---|---|---|---|
| 1 | Product/repository name & owner/organization | Product owner | Phase 1 repo creation |
| 2 | Deployment jurisdiction(s) & data-residency constraints | Legal/security | Phase 8 cloud, production hosting |
| 3 | Regulatory/quality framework + responsible advisor (separate legal/quality workstream) | Clinical/quality owner | Any production claim (Phase 10+) |
| 4 | First analyzer exact identity + licensed interface documentation | Product owner / biomedical | Phase 10 physical integration |
| 5 | Target LIS/HIS protocol/profile & sample contracts | LIS/HIS integrator | Phase 6 adapters |
| 6 | Data retention/deletion policy; whether cloud may ever hold clinical payloads | Data governance | Phase 8/9 storage |
| 7 | Windows/macOS code-signing identities | Security administrator | Phase 11 release, signed installers |
| 8 | Production availability & recovery objectives (RTO/RPO/uptime) | Operator/product owner | Phase 11/12 go/no-go |
| 9 | Named accountable roles (owner, tech lead, clinical safety, security, release approver) | Product owner | Phase 0 exit gate |

None of the above blocks Phase 1 scaffolding or the simulator-only ASTM vertical slice. Each must be resolved before the phase named in its "Gates" column.

---

## 13. Constraints and reminders

- Synthetic / de-identified data only; no real patient data anywhere in this project.
- No clinical validity is claimed from parsing or simulation alone.
- No regulatory or compliance status (FDA/CE/IVDR/HIPAA/GDPR/ISO) is asserted; certification is a separate legal/quality workstream (OPEN item 3).
- The gateway is passive by default; capture-only cannot transmit.
- Vendor protocol manuals and confidential documents are never committed to the repository.
- This PRD is subordinate to `DEVELOPMENT_PLAN.md`, `README.md`, and `OPERATOR_CHECKLIST.md`; if any conflict is found, those documents win and this PRD is corrected.
