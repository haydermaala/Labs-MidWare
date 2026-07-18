# Threat Model — lab-connect Laboratory Analyzer Middleware

Status: Draft (Phase 0)
Date: 2026-07-18

> This is a Phase 0 planning artifact. It records the threats and the mitigations
> already mandated by `DEVELOPMENT_PLAN.md` (see §2.3 trust boundaries, §6 security
> and privacy baseline, §5 testing strategy). It does not assert any regulatory or
> compliance status and does not claim conformance to any standard. Items requiring
> a decision are marked **OPEN:**.

---

## 1. Scope and method

This document applies the STRIDE method (Spoofing, Tampering, Repudiation,
Information disclosure, Denial of service, Elevation of privilege) to the
lab-connect system: the Rust edge service (`gatewayd`), the Tauri technician
desktop app, the ASP.NET Core control plane, the driver registry, and the
LIS/HIS adapters.

Guiding principles carried from the development plan:

- **Persist before process.** Raw bytes are stored durably before any parsing,
  normalization, or delivery (§4 pipeline: receive → persist raw → parse → ...).
- **Passive by default.** Until a device profile is reviewed and validated, the
  gateway operates in capture-only mode and cannot transmit to the device
  (README safety boundary; §1.3 support levels; §3 acceptance).
- **Deny by default.** Least privilege on network, file, and device access (§6).
- **Untrusted input.** The analyzer VLAN and every byte entering a transport
  adapter are untrusted (§2.3).
- **PHI/telemetry separation.** Patient/result payloads are kept apart from
  operational telemetry; identifiers and result values never enter metric labels
  (§2.3, §6, §8).

Clinical validity is never established by parsing or simulation alone. No real
patient data appears in the repository, CI, fixtures, logs, screenshots, or
diagnostic bundles (§3.1, §5.2, §6).

---

## 2. Assets

| Asset | Description | Sensitivity | Where it lives |
|---|---|---|---|
| Raw analyzer messages | Untrusted bytes captured from serial/TCP/MLLP/file; **may contain PHI** | High (treat as PHI) | Edge SQLite (append-oriented, encryption support), retained per policy |
| Parsed/canonical results | Normalized `ResultSet`/`Result` clinical payloads | Highest (clinical) | Edge SQLite; control-plane PostgreSQL only if governance approves (**OPEN**) |
| PHI / patient identifiers | `PatientReference`, specimen and order identifiers | Highest | Same as canonical results; never in logs/metrics/telemetry |
| Driver packages | Signed, versioned, hash-verified data-first packages | High (integrity-critical) | Driver registry, R2 (encrypted, per-env buckets) |
| Driver signing keys | Keys used to sign driver packages and releases | Critical (integrity root) | Dedicated secret store; **OPEN:** signing identities |
| Device/gateway certificates | mTLS/device certs for gateway↔cloud, with rotation | Critical | Edge secure store; control plane enrollment |
| Tenant data | Org/site/lab/user/role, configuration, mappings, fleet inventory | High | Control-plane PostgreSQL |
| Audit / security events | Tamper-evident `AuditEvent`/`SecurityEvent` records | High (integrity-critical) | Edge SQLite and control-plane PostgreSQL |
| Credentials / tokens | Bootstrap tokens, rotated device creds, OIDC tokens, service tokens, presigned URLs | Critical | Secret stores only; never in source/fixtures/logs/bundles |

---

## 3. Trust boundaries

Derived from §2.3 and the logical architecture in §2.1.

1. **Analyzer VLAN → transport adapter.** Everything on the analyzer side is
   untrusted input. Bytes cross into `gatewayd` here.
2. **Transport adapter → session/parser.** Untrusted frames are assembled and
   parsed; the parser boundary is a fuzz/property-test target.
3. **Driver package → driver runtime.** Packages are data-first and must be
   signature- and hash-verified before load; advanced transforms run sandboxed.
4. **Technician app → local gateway API.** Loopback/IPC only, authenticated;
   the browser/UI never receives raw device credentials.
5. **Gateway → cloud control plane.** Mutually authenticated TLS / device
   certificates with rotation; outbound-initiated so most sites need no inbound
   port.
6. **Control-plane API ↔ operator/browser.** Multi-tenant; tenant isolation and
   RBAC enforced server-side.
7. **Update / driver channel.** Signed metadata and signed packages, staged
   channels (stable/canary), rollback available.
8. **Support access.** Elevated human access to gateways/bundles; audited,
   role- and reason-gated.

---

## 4. Attack surfaces and STRIDE analysis

Each surface lists threats by STRIDE category with the mitigations already
mandated by the plan. Where a control is not yet decided it is marked **OPEN:**.

### 4.1 Analyzer VLAN — untrusted bytes (serial / TCP / file inputs)

| STRIDE | Threat | Mitigation (mandated) |
|---|---|---|
| Spoofing | Rogue device or host impersonates a certified analyzer on the VLAN | TCP allowlists/bind controls; stable device identifiers where OS permits; capture-only until a profile is validated; no clinical release from unidentified equipment |
| Tampering | Injected/altered frames, corrupted checksums, forged sequence numbers | Persist-before-process of raw bytes; checksum/sequence validation; framing state machine; unknown fields/records preserved losslessly, never silently rewritten |
| Repudiation | No evidence of what a device actually sent | Append-oriented raw-message store; every result links to raw payload, parser/driver version, mapping version, validation decision, delivery attempt, ACK (§2.4) |
| Info disclosure | Raw messages carry PHI onto shared infrastructure | Treat raw messages as PHI; encryption support at rest; PHI redacted from default views/logs; PHI/telemetry separation |
| DoS | Oversized frames/messages, connection floods, unbounded buffers, checksum storms | Bounded buffers/queues; max-frame/message limits; connection-flood protection; idle/session timeouts; backpressure; backoff with jitter |
| Elevation | Malformed input triggers memory corruption / code execution in parser | Rust memory safety; parsers must never panic on malformed input (§4 acceptance); property/fuzz tests on every PR (§5) |

### 4.2 HL7 MLLP listener

| STRIDE | Threat | Mitigation (mandated) |
|---|---|---|
| Spoofing | Unauthenticated peer connects to the MLLP listener | Connection controls/allowlists; TLS where applicable; bind controls |
| Tampering | Malformed MLLP framing, split/oversized messages, encoding attacks | MLLP limits; length/encoding validation; ACK modes; lossless parse of supported ORU/OML/OBR/OBX/SPM/SAC subsets |
| Repudiation | Dispute over received/acknowledged messages | Idempotency keys, correlation IDs, delivery receipts, ACK/NAK records, reconciliation (§6 HL7) |
| Info disclosure | PHI in HL7 payloads leaks to logs/telemetry | Default redaction; PHI/telemetry separation; no identifiers/result values in metric labels |
| DoS | Connection floods, slow-loris, unbounded message size | Bounded queues, timeouts, max-message limits, backpressure, retry policy with dead-letter |
| Elevation | Parser exploited via crafted segment | Bounded, non-panicking parser; fuzz tests; deny-by-default |

### 4.3 Driver package ingestion

| STRIDE | Threat | Mitigation (mandated) |
|---|---|---|
| Spoofing | Attacker submits a package posing as a trusted vendor driver | Signed packages; signature verification against approved signing identities (**OPEN:** signing identities); dual approval for certified driver publication |
| Tampering | Modified package contents / manifest / fixtures | Package digest + signature; hash verification; versioned packages; installation audit; rejection on tamper (§7 acceptance) |
| Repudiation | Unclear who published/promoted a driver | Installation audit; promotion states with review/approval; `Signature` entity linked to `DriverVersion` |
| Info disclosure | Package or bundle embeds secrets or confidential vendor docs | Secrets never in packages/fixtures/bundles; no confidential vendor documents committed (§3.1) |
| DoS | Archive/zip bombs, oversized manifests, resource-exhausting fixtures | Validate archives, lengths, encodings, paths; bounded extraction; sandbox CPU/memory/IO/time limits |
| Elevation | Package ships executable transform that escapes to host | Data-first packages default to no executable code; advanced transforms sandboxed with strict CPU/memory/IO/time limits; no unrestricted scripts |

### 4.4 Gateway ↔ cloud channel

| STRIDE | Threat | Mitigation (mandated) |
|---|---|---|
| Spoofing | Fake gateway enrolls, or fake cloud endpoint harvests data | Mutually authenticated TLS / device certificates; short-lived bootstrap tokens; rotated device credentials (§8) |
| Tampering | MITM alters configuration or results in transit | mTLS integrity; signed configuration; signed driver/update metadata |
| Repudiation | Disputed enrollment or config change | Enrollment and support-access audit; configuration versions; immutable-enough audit for the agreed threat model (§8 acceptance) |
| Info disclosure | Clinical payloads exposed to cloud that should not hold them | PHI/telemetry separation; minimal data collection by default; **OPEN:** whether cloud may ever hold clinical payloads |
| DoS | Flood of enrollment/telemetry requests | Rate limits; bounded queues; backpressure |
| Elevation | Compromised gateway cert grants broad cloud access | Least privilege per gateway; certificate rotation; tenant isolation; certificate-compromise exercise (§11) |

### 4.5 Control-plane API

| STRIDE | Threat | Mitigation (mandated) |
|---|---|---|
| Spoofing | Credential stuffing / token replay against the API | OIDC authentication; short-lived rotated credentials; never share personal browser cookies with tools |
| Tampering | Cross-tenant write, mapping/config tampering | Server-side RBAC; tenant isolation tests; dual approval for production mapping changes and certified driver publication |
| Repudiation | Denial of admin/mapping/approval actions | Tamper-evident audit for configuration, mapping, driver, access, delivery events |
| Info disclosure | Cross-tenant read of PHI/tenant data (IDOR) | Deny-by-default authorization; tenant isolation tests (§5, §8) |
| DoS | Expensive queries / request floods | Rate limits; bounded work; backpressure |
| Elevation | Role escalation across separated duties | Separated roles (technician, mapping reviewer, clinical approver, operator, security admin, auditor); least privilege |

### 4.6 Technician local API (loopback / Tauri IPC)

| STRIDE | Threat | Mitigation (mandated) |
|---|---|---|
| Spoofing | Local malware calls the loopback API | Authenticated loopback API / IPC; first-run security setup; local API authentication (§5, §11) |
| Tampering | Unauthorized config/mapping change from the desktop | Role- and reason-gated sensitive access; audit of changes |
| Repudiation | No record of local operator actions | Local audit trail per pipeline step (§5 acceptance) |
| Info disclosure | PHI/secrets shown in default views or exported bundles | Default PHI/secret redaction; explicit role + reason for sensitive access; redacted diagnostic bundles |
| DoS | UI-driven overload of the gateway | Per-device isolation; bounded queues; backpressure |
| Elevation | UI process gains gateway service privileges | UI is local-only, separate from the service; least-privilege service accounts; browser never receives raw device credentials |

### 4.7 Support access

| STRIDE | Threat | Mitigation (mandated) |
|---|---|---|
| Spoofing | Attacker uses support pathway to reach a tenant/gateway | Authenticated, least-privilege support access; short-lived credentials |
| Tampering | Support action changes clinical config without trace | Support-access audit; role/reason gating; dual approval for sensitive changes |
| Repudiation | Disputed support action | Immutable-enough support-access audit (§8) |
| Info disclosure | Support bundle leaks PHI/secrets | Redact PHI and secrets from support bundles by default (§6); approved bundles only |
| DoS | Support tooling exhausts gateway resources | Bounded operations; per-device isolation |
| Elevation | Support role over-privileged | Least privilege; separated auditor/security-admin roles |

### 4.8 Update channel

| STRIDE | Threat | Mitigation (mandated) |
|---|---|---|
| Spoofing | Malicious update source impersonates the release channel | Signed update metadata; Windows code signing; macOS Developer ID/notarization (§11) |
| Tampering | Modified installer/update payload | Signed packages, checksums, reproducible release manifest, SBOM, provenance |
| Repudiation | Unclear which release was applied | Release provenance; version/update status tracked; software-release records (§2.4, §8) |
| Info disclosure | Update metadata or logs leak secrets | Secrets never in logs/artifacts; secrets not exposed to PRs or logs (§9) |
| DoS | Bad update bricks or floods gateways | Staged channels (stable/canary); rollback package; edge stays functional offline |
| Elevation | Rollback to a known-vulnerable version | Versioned rollback with revocation; failed-update runbook (§8, §11) |

---

## 5. Misuse / abuse cases and matching negative tests

These map directly to the §5.1 security test line ("authz, tenant isolation,
SSRF/path traversal, package tampering, secret leakage, resource exhaustion")
and to the fault-injection and property/fuzz lines. Each must have an explicit
**negative test** that asserts the attack fails safely.

| # | Misuse / abuse case | Negative test (expected: rejected/contained, no PHI/secret leak, no panic) |
|---|---|---|
| M1 | SSRF via file/URL/config path pointing at internal or metadata endpoints | Reject non-allowlisted destinations; path/URL allowlists enforced; no outbound to internal ranges |
| M2 | Path traversal in file-watcher input or package extraction (`../`, absolute paths) | Path allowlists; extraction confined to sandbox dir; traversal attempts rejected |
| M3 | Zip/archive bomb in a driver package | Bounded decompression (size/ratio/time caps); ingestion aborts and rejects package |
| M4 | Oversized frames / HL7 messages | Max-frame/message limits enforced; connection dropped or NAK'd; buffers stay bounded |
| M5 | Malformed encodings / corrupted frames / bad checksums | Parser never panics; malformed input rejected losslessly; property/fuzz smoke on every PR |
| M6 | Connection flood (serial reconnect storm, TCP/MLLP flood) | Connection limits, timeouts, backoff-with-jitter; service stays responsive; capture-only cannot transmit |
| M7 | Package tampering (altered bytes, forged/absent signature) | Signature + hash verification fails → package rejected; installation audit records rejection |
| M8 | Secret leakage in logs / metrics / crash reports / support bundles | Automated secret scanning; redaction assertions; no secret in any emitted artifact |
| M9 | Resource exhaustion (disk full, unbounded queue, memory spike, clock shift) | Bounded queues + backpressure; disk-pressure/clock-shift fault injection; graceful degradation, no loss in tested paths |
| M10 | Cross-tenant access (IDOR) attempt on control-plane API | Tenant isolation tests; deny-by-default authz; cross-tenant read/write blocked |
| M11 | PHI in metric labels or telemetry | Assert no patient identifier or result value appears in metric labels or telemetry streams |
| M12 | Replay / duplicate delivery | Idempotency keys, dedupe keys, durable outbox; duplicate does not create duplicate delivery |
| M13 | Unauthorized outbound command to a device (see §6) | Passive default; capability allowlist; blocked without separate recorded approval |

---

## 6. Outbound-to-device safety

Outbound communication toward analyzers is the highest-consequence action in the
system and is constrained beyond ordinary RBAC (§1.2 non-goals, §11 risks,
README safety boundary).

- **Passive default.** The gateway starts and remains in capture-only mode for
  any device until its profile is reviewed and validated. Capture-only mode is
  structurally unable to transmit (§3 acceptance).
- **No probing of unknown equipment.** No arbitrary probing or control commands
  to unidentified devices (§1.2).
- **Capability allowlist.** Any outbound message type (queries, orders) must be
  explicitly declared and allowlisted by the validated driver capabilities; not
  on the allowlist means not permitted.
- **Separate approval.** Enabling outbound orders requires that inbound results
  are fully validated first and a **separate, recorded approval** is captured
  (§10 step 7). This approval is distinct from mapping or driver approval and is
  audited.
- **Blast radius.** Per-device isolation limits the effect of any outbound
  fault to a single device.

---

## 7. Residual and open items

The following require decisions outside this document and are flagged so they are
not silently assumed:

- **OPEN:** Deployment jurisdiction and applicable privacy law (GDPR / HIPAA /
  other) — not asserted here. Determines several controls below.
- **OPEN:** Retention periods for raw messages, canonical results, audit, and
  bundles.
- **OPEN:** Whether the cloud control plane may ever hold clinical payloads.
- **OPEN:** At-rest encryption specifics per environment (edge SQLite,
  PostgreSQL, R2 buckets, backups).
- **OPEN:** Signing identities for driver packages and releases.
- **OPEN:** Security-owner contact and incident-response SLA.
- **OPEN:** Regulatory / healthcare certification status — treated as a separate
  legal/quality workstream (§6, §11), **not** a software checkbox, and **not**
  asserted or implied by this document.

This model is a living Phase 0 draft and is subject to the independent
threat-model review scheduled in §11.
