# lab-connect — Validation Strategy

Status: Draft (Phase 0)
Date: 2026-07-18

> Governing authority for this document: `DEVELOPMENT_PLAN.md` (§1.3 support levels, §2.5 driver lifecycle, §4 phased plan, §5 testing strategy, §10 definition of MVP complete, Phase 10 physical analyzer) and `README.md`. Where this document and the development plan disagree, the development plan wins and this document must be corrected.

---

## 0. Purpose and scope

lab-connect is safety-critical middleware: it moves laboratory results between analyzers and LIS/HIS systems, and a wrong, lost, duplicated, or misattributed result can cause clinical harm. This document defines how we decide that a driver/profile is trustworthy enough to carry a real patient result, and how we keep that trust as software changes.

It covers two activities that are routinely and dangerously conflated:

- **Software testing** — evidence that the code behaves as engineered (parsing, framing, normalization, delivery, fault handling).
- **Clinical validation** — evidence, signed by authorized laboratory staff, that for a specific analyzer, firmware, workflow, and destination profile, the results lab-connect produces match the truth the instrument reported and the existing validated workflow.

This document does not authorize any deployment, cloud provisioning, real-patient use, analyzer control command, or external-account write. Those remain gated by the development plan's phase gates and by human approval.

---

## 1. The critical distinction: software testing is NOT clinical validation

**A passing test suite never makes a profile clinically valid. Simulated evidence never makes a profile clinically valid.**

Software tests answer: *does the code do what the engineer intended, on the inputs we imagined?* Clinical validation answers a different and larger question: *does the code, connected to a specific real instrument in a specific real workflow, produce results a laboratory director will stake a patient's care on?*

The two failure modes are independent:

| Failure class | Caught by software testing? | Caught by clinical validation? |
|---|---|---|
| Checksum/framing bug, parser panic, lost message on restart | Yes | Only incidentally |
| Wrong assumption about a field's meaning in the real device | No — the code passes its own tests | Yes |
| Unit mismatch (e.g. mg/dL vs mmol/L) the fixture author never questioned | No | Yes |
| Reference range / flag semantics differ from the real instrument | No | Yes |
| Result-to-patient/specimen linkage correct in simulator, wrong onsite | No | Yes |
| Real device emits an undocumented record variant | No — fixture was synthetic | Yes |

Consequences that are binding on this project:

- Green CI, 100% coverage, and a fully passing simulator run are **necessary but not sufficient** to promote a driver past `validated`. They are entry conditions to clinical validation, not substitutes for it.
- No document, changelog, UI label, release note, or status field may describe a driver as clinically valid, certified, or production-ready on the basis of parsing success or simulated tests alone. Doing so is a defect to be fixed.
- Clinical validity is always scoped to **a specific analyzer model + firmware range + workflow + destination profile + site**. It does not generalize to another model, firmware, workflow, profile, or site without new evidence.

---

## 2. Driver / profile status lifecycle

Per `DEVELOPMENT_PLAN.md` §2.5, driver state progresses:

`draft → parsed → mapped → validated → certified → deprecated/revoked`

Each transition is a **gate**: it requires specific evidence and specific approvers, and it can only be entered when the prior state's evidence is intact. A driver can be **deprecated** or **revoked** from any post-`draft` state (revocation is immediate and removes production authorization; see §2.2).

### 2.1 Lifecycle gate table

| From → To | What it means | Required evidence | Approver(s) | Can it touch real patient data? |
|---|---|---|---|---|
| — → **draft** | Package skeleton exists: manifest, declared transports/protocol family, placeholder rules. | Manifest validates against schema; repository conventions met; synthetic fixtures only. | Engineering author | No |
| **draft → parsed** | Recorded/synthetic samples parse into the lossless intermediate representation without panic. | Unit + property/fuzz tests green on parsers; negative fixtures fail as expected; golden vectors parse. | Engineering author + peer reviewer | No |
| **parsed → mapped** | Parsed records are mapped into the canonical model with defined units, terminology, flags, provenance. | Mapping fixtures: expected normalized object matches; unit/terminology mappings reviewed; provenance links present. | **Mapping reviewer** (separate from author) | No |
| **mapped → validated** | Full software evidence complete: contract, integration, end-to-end via simulator, and fault-injection all pass for this driver. | Full test pyramid green (§4); simulator scenarios from the package pass; fault-injection suite passes; coverage of safety-critical modules meets threshold (**OPEN:** thresholds, §11). | Mapping reviewer + QA/automation | No — simulator/synthetic only. **"validated" here means software-validated, NOT clinically validated.** |
| **validated → certified** | Clinical validation on the real instrument completed and signed; driver may carry real patient results at approved site(s). | Signed clinical validation report (§6): isolated-network capture, controlled cases with known expected outputs, side-by-side vs an existing validated workflow, required case coverage (§7), de-identified fixtures, laboratory sign-off. | **Clinical approver** (dual-approval with mapping reviewer; both separate from the engineer who wrote the code — §8). Plus release approver for publication. | Yes, at the approved site(s) only; site-limited until wider evidence exists. Outbound orders require a **separate** approval (§6). |
| **certified → deprecated** | Superseded; no new installs, existing installs supported for a defined window. | Replacement identified or sunset plan; migration path documented. | Release approver + clinical approver | Existing sites per sunset plan only |
| any → **revoked** | Withdrawn immediately (defect, tampering, safety signal, key compromise). | Revocation reason recorded; revocation propagated to gateways; audit entry. | Security owner or clinical approver (either may trigger); release approver records | No — revocation removes production authorization immediately |

**"validated" vs "certified" is the exact line between software testing and clinical validation.** Everything up to and including `validated` is achievable with synthetic data and simulators. `certified` cannot be reached without real-instrument clinical evidence and laboratory sign-off.

### 2.2 Revocation and downgrade

- Revocation is immediate and takes precedence over any in-flight promotion.
- A driver that fails a re-validation, or whose real-world evidence is contradicted, is downgraded (at minimum to `validated`, losing production authorization) or revoked. Downgrade/revocation reasons are recorded in the audit trail and surfaced to affected sites.
- A material change to a `certified` driver (mapping change, parser change affecting clinical fields, firmware-range expansion) invalidates certification for the changed scope and requires re-entry through the gates for that scope.

### 2.3 Support levels and promotion gates

Per `DEVELOPMENT_PLAN.md` §1.3, every driver carries one of four support levels. Support level and lifecycle state are related but distinct: the lifecycle state is where a driver is in its evidence journey; the support level is the operational promise made to a site.

| Support level | Meaning (per §1.3) | Corresponding lifecycle state(s) | May enter production? | Promotion gate to reach it |
|---|---|---|---|---|
| **Capture-only** | Unknown device; receive and retain bytes without clinical interpretation. | draft / parsed | No — bytes retained, no interpretation, cannot transmit clinical results. | Default for any unidentified device; passive capture must be provably unable to transmit (Phase 3 acceptance). |
| **Experimental** | Parses recorded samples only; cannot enter production. | parsed / mapped | No. | Parsers pass on recorded/synthetic samples; explicitly barred from production by policy and by tooling. |
| **Compatible** | Protocol family tested; **site validation required.** | validated | Only after per-site clinical validation → then site-limited certified. | Full software evidence (validated gate). Production still blocked until the site completes clinical validation (§6) and the driver is certified for that site. |
| **Certified** | Exact model/firmware/workflow validated and signed. | certified | Yes, at approved site(s), site-limited. | The `validated → certified` gate: signed clinical validation report + dual clinical approval. Certified driver publication requires **dual approval** (§8, and §6 security baseline). |

Rules that bind the level machinery:

- A device is **Capture-only by default** until reviewed and validated (README safety boundary; §1.2 non-goal "No autonomous activation of inferred drivers").
- **Experimental and Capture-only can never enter production**, regardless of how green the software tests are. The tooling must enforce this, not just documentation.
- **Compatible is not a licence to run clinically.** "Protocol family tested" is software evidence; each site must still clinically validate before the driver is certified for that site.
- Promotion is one gate at a time. No skipping from Experimental straight to Certified.

---

## 3. Test pyramid

Per `DEVELOPMENT_PLAN.md` §5.1. Layers are ordered by breadth (many, fast, low) to depth (few, slow, high-fidelity). All repository/CI data is synthetic or irreversibly de-identified (§5).

1. **Unit** — framing, checksums, ASTM/HL7 parsing, record/field extraction, mappings, deduplication keys, state transitions. Deterministic; virtual clock for time-dependent logic.
2. **Property / fuzz (untrusted parsers)** — every parser that consumes bytes off an untrusted transport (analyzer VLAN is untrusted input, §2.3) is fuzzed: length boundaries, malformed encodings, truncated/oversized frames, injected control characters. **Malformed input must never panic** (Phase 4 acceptance). Fuzz smoke runs on every PR; longer campaigns are scheduled (§5.3).
3. **Contract** — canonical schema round-trips, REST contracts, HL7 v2 profile subsets (ORU/OML/ORM/ACK/OBR/OBX/SPM/SAC), driver manifest schema, and **backward-compatibility** of versioned contracts.
4. **Integration** — serial loopback, TCP client/server, file watcher, SQLite schema/migrations (upgrade + rollback), API↔database↔object-storage.
5. **End-to-end via simulator** — analyzer simulator → gateway pipeline (receive → persist raw → parse → normalize → validate → map → deduplicate → queue → deliver/hold) → mock LIS/HIS → control plane. This exercises the whole vertical slice on **synthetic** data. It is the highest-fidelity *software* evidence and is still not clinical validation.
6. **Fault injection** — deterministic faults with expected recovery. Required cases: **dropped ACK, corrupted frame, disk full, clock shift, restart, network partition, certificate expiry.** Each must demonstrate no message loss in validated paths, correct retry/reconciliation, and no duplicate delivery.
7. **Platform (Windows / macOS)** — port naming/enumeration, permissions, service (Windows Service / launchd) lifecycle, suspend/resume, USB removal/reinsert, installer/update smoke. Windows is the first certified production target; macOS compatibility is certified per analyzer.
8. **Security** — authz, tenant isolation, SSRF/path traversal, driver-package tampering/signature verification, secret-leakage checks (source, fixtures, logs, screenshots, diagnostic bundles), resource exhaustion / bounded-queue backpressure.

Sitting above the pyramid, not inside it: **clinical validation** (§6) — expected/actual comparison on the real instrument, signed by authorized laboratory staff. It is a separate activity with separate evidence and separate approvers.

### 3.1 Fault-injection matrix

| Fault | Injected where | Expected behavior | Assertion |
|---|---|---|---|
| Dropped ACK | Link layer / MLLP | Retry per policy; no duplicate delivery on eventual success | Delivery-once; ack reconciliation consistent |
| Corrupted frame | Transport / parser | NAK or reject; no panic; raw bytes retained | Parser survives; evidence preserved |
| Disk full | Durable queue / raw store | Backpressure, no silent drop, clear operator signal | 0 message loss; bounded resource use |
| Clock shift | Virtual clock | Timeouts/sequence logic stable; no premature expiry/dedupe error | Deterministic state |
| Restart (gateway) | Process kill mid-pipeline | Resume from durable state; in-flight message not lost or duplicated | Queue recovery verified |
| Network partition | Gateway↔LIS / gateway↔cloud | Store-and-forward; reconcile on reconnect | 0 loss; offline operation intact |
| Certificate expiry | Gateway cert / TLS | Fail closed with clear alert; no insecure fallback | No silent insecure transmit |

---

## 4. Required fixture metadata and the synthetic-only rule

**Synthetic-only rule (binding):** every fixture in the repository, CI, staging, and any diagnostic bundle is **synthetic or irreversibly de-identified**. No real patient data. No production patient data (§1.2). No protocol manual or confidential vendor document is committed (§3.1). Secrets never appear in fixtures, screenshots, logs, or bundles (§3.1, §6).

Per `DEVELOPMENT_PLAN.md` §5.2, every fixture carries this metadata:

| Field | Purpose |
|---|---|
| **Source context** | Where the sample pattern came from (synthetic-authored, de-identified capture from isolated test network, simulator scenario). Never links to a real patient/specimen. |
| **Protocol / driver version** | Exact protocol family + version and the driver/parser version the fixture is pinned to, so re-runs are reproducible. |
| **Expected parse** | The lossless intermediate representation the parser must produce (including preserved unknown fields/raw records). |
| **Expected normalized object** | The canonical-model object after mapping — values, units, flags, provenance links. |
| **Expected validation / delivery decision** | What canonical validation and delivery routing must decide (accept/hold/dead-letter; deliver/duplicate-suppressed), so a change in decision is caught. |

Fixture rules:

- De-identified captures from a physical analyzer (Phase 10) must be de-identified **before** they enter the repository; the isolated-network capture stays out of version control until de-identification is verified.
- Negative fixtures (malformed, checksum failures) declare their expected failure so "it broke correctly" is asserted, not assumed.
- A fixture is not evidence of clinical validity. Fixtures — even de-identified real captures — support software testing and reproducibility; clinical validity comes only from §6.

---

## 5. Quality gates per pull request

Per `DEVELOPMENT_PLAN.md` §5.3 and §3.1. Every PR must pass:

- Formatting, lint, and type checks (Rust, TypeScript, .NET).
- Unit, integration, and conformance tests.
- Parser **fuzz smoke** on every PR (longer campaigns scheduled).
- Contract backward-compatibility tests.
- Dependency, license, secret, and code scans; SBOM generation for releases.
- Coverage thresholds focused on **safety-critical modules** (framing, checksums, parsers, mapping, deduplication, delivery/reconciliation) — not a single vanity percentage. **OPEN:** exact thresholds per safety-critical module (§11).
- Windows/macOS packaging smoke tests before release.
- **Manual approval** for staging/production deployment and for signed driver promotion. Certified driver publication and production mapping changes require **dual approval** (§8).

No PR may raise a driver's advertised support level or lifecycle state as a side effect of code changes; level/state promotion happens only through the §2 gates with the named approvers.

---

## 6. Clinical validation procedure — physical analyzer

Applies to Phase 10 (first physical analyzer integration) and to every later analyzer. This is the procedure that moves a driver from `validated` (software) to `certified` (clinically). It cannot be run against a simulator; it requires the real instrument.

**Preconditions (all must hold before starting):**

- Legal access to the correct host-interface documentation and confirmed interface licensing (§4.10, Phase 10 step 1). We do not reverse-engineer beyond authorization.
- The driver is at lifecycle state `validated` with full software evidence intact (§2, §3).
- A regulatory/quality framework decision has been made or explicitly deferred. **OPEN:** which regulatory/quality framework governs this validation (e.g. IEC 62304 / ISO 13485 / IVDR). This document does **not** assert that any specific framework applies; framework applicability is a **legal/quality workstream**, not a software checkbox (§6 baseline), and must be resolved before certification is claimed for regulated use.
- The named **clinical approver** is identified. **OPEN:** identity of the responsible clinical/quality approver (§11).

**Procedure (in order):**

1. **Isolated test network.** Establish an isolated network; the analyzer VLAN is untrusted. No connection to production LIS/HIS. No outbound writes.
2. **Passive capture first.** Operate the driver in capture-only mode. Observe real framing, record variants, and traffic without any transmission. Confirm capture-only provably cannot transmit.
3. **Controlled cases with known expected outputs.** Run instrument cases whose correct results are known in advance (controls, characterized samples). Record actual vs expected for every case in the coverage matrix (§7).
4. **De-identify exported fixtures.** Any capture retained as a fixture is irreversibly de-identified before leaving the isolated environment, and carries full fixture metadata (§4).
5. **Side-by-side comparison against an existing validated workflow.** Run the same specimens through the current, already-validated laboratory workflow and compare, result by result. Discrepancies must be explained and resolved, not averaged away.
6. **Laboratory sign-off.** Authorized laboratory staff review the expected/actual evidence and sign the validation report. Sign-off is by the clinical approver, who is organizationally separate from engineering (§8).
7. **Site-limited certification.** On sign-off, the driver may be certified **for that site only**. It remains site-limited until wider evidence exists; another site repeats site validation before it is certified there (support level "Compatible" → site-certified).
8. **Outbound orders only after separate approval.** Inbound results must be fully validated first. Enabling outbound orders/queries to the analyzer requires a **separate, explicitly recorded approval** — it is not implied by inbound certification (§1.2 non-goal on device commands; Phase 10 step 7).

**Exit (Phase 10 acceptance):** signed validation report; **zero unexplained discrepancies**; rollback and downtime SOPs rehearsed; profile remains site-limited until wider evidence exists.

**OPEN:** first analyzer identity — exact model, firmware, host-interface revision (§11). **OPEN:** target LIS/HIS profile the results are delivered into (§11). Both must be fixed in Phase 0 before this procedure can be scheduled.

---

## 7. Required clinical case coverage matrix

Per `DEVELOPMENT_PLAN.md` Phase 10 step 5. Each case that is applicable to the device must have a known expected output, be run on the real instrument, and be compared side-by-side (§6). "N/A" is permitted only with a recorded rationale from the clinical approver.

| # | Case | What it proves | Expected-output source | Notes |
|---|---|---|---|---|
| 1 | **Normal** | Ordinary in-range result normalizes, maps, and delivers correctly. | Known control / characterized sample | Baseline |
| 2 | **Abnormal** | Out-of-range value + flag semantics preserved. | Characterized abnormal sample | Verify flags/reference ranges |
| 3 | **Critical** | Critical/panic value handled and flagged; no silent downgrade. | Characterized critical sample | Highest patient-safety weight |
| 4 | **Text** | Textual/comment results carried without loss or misparse. | Instrument text result | Free-text handling |
| 5 | **Rerun** | Repeat of a test resolves correctly; no false duplicate. | Instrument rerun | Dedup vs legitimate repeat |
| 6 | **Dilution** | Dilution factor / adjusted result handled correctly. | Diluted sample | Unit/factor correctness |
| 7 | **Correction** | Corrected result supersedes the prior value with provenance. | Instrument correction/amend | Correction chain preserved |
| 8 | **QC** | Quality-control result routed/handled distinctly from patient results. | QC material | QC not delivered as patient result |
| 9 | **Unknown barcode** | Unrecognized specimen ID handled safely (hold, not misattribute). | Deliberate unknown ID | No wrong patient linkage |
| 10 | **Timeout** | Instrument/link timeout recovers without loss or duplication. | Injected timeout | Ties to fault injection §3.1 |
| 11 | **Duplicate** | Duplicate message does not create duplicate delivery. | Replayed message | 0 duplicate release |
| 12 | **Restart** | Gateway restart mid-flow loses nothing, duplicates nothing. | Kill during flow | Queue recovery verified |

Cases 10–12 overlap the software fault-injection suite (§3.1); in clinical validation they are exercised on the real instrument and confirmed by side-by-side comparison, not only in the simulator.

---

## 8. Roles and dual approval

Per `DEVELOPMENT_PLAN.md` §6 (separate roles; dual approval for certified driver publication and production mapping changes) and §9 (clinical approval and production authorization remain distinct from engineering).

| Role | Owns | Must be separate from |
|---|---|---|
| Engineering author | Writing driver/profile code, parsers, mappings, tests | — |
| **Mapping reviewer** | Reviewing field/unit/terminology mappings and the normalized object; approves `parsed → mapped` and co-approves `mapped → validated` | The engineering author of that driver |
| **Clinical approver** | Signing the clinical validation report; approving `validated → certified`; separate outbound-order approval | Engineering (organizationally separate; §9) |
| QA / automation | Owns the test pyramid and fault-injection evidence; co-approves `validated` | — |
| Release approver | Signed publication/promotion of drivers and releases | — |
| Security owner | Signature/tamper/revocation controls; may trigger revocation | — |

**Dual-approval rule:** promoting a driver to `certified` and publishing it, and any production mapping change, require **two distinct approvers** — the mapping reviewer and the clinical approver — neither of whom is the engineer who authored the change. A single person may hold several *engineering* roles early in the project, but clinical approval and production authorization must not collapse into the engineering role (§9).

**OPEN:** the named individual(s) holding the clinical/quality approver role and their reporting line (§11).

---

## 9. MVP-complete and go / no-go criteria

Per `DEVELOPMENT_PLAN.md` §10 (definition of MVP complete) and §4 Phase 12 (go/no-go metrics). These are release-blocking; each must have evidence, not assertion.

**MVP-complete requires (all of):**

- Signed Windows development/pilot installer and a demonstrated notarized macOS test-installer strategy.
- Gateway service survives UI closure, restart, and internet outage.
- Serial/TCP/file transports and the ASTM engine pass conformance and fault tests.
- A synthetic result traverses to the mock LIS with complete audit/provenance and **no duplicate delivery**.
- Generic ASTM driver lifecycle, signing verification, rollback, and simulator all work.
- Technician can configure, capture, map, validate, and export a **redacted** diagnostic bundle.
- Staging control plane manages one gateway **without exposing PHI**.
- Security review, restore, update-rollback, and incident/downtime runbooks have evidence.
- **One physical analyzer profile has completed controlled clinical validation (§6) before any production claim.**

**Go / no-go metrics (release-blocking):**

| Metric | Threshold |
|---|---|
| Message loss in validated paths | **0** |
| Unexplained clinical discrepancies | **0** |
| Duplicate result release | **0** |
| Queue recovery after restart/partition | **Verified** |
| Acknowledgement reconciliation | **Complete** (every delivery attempt reconciled to an ack, NAK, or dead-letter) |
| Operational availability target | Met (**OPEN:** target value, §11) |
| Support / rollback readiness | Confirmed |

Any non-zero on the first three, or an unverified queue-recovery / incomplete ack-reconciliation, is an automatic **no-go**.

---

## 10. Non-negotiable rules (summary)

1. Software tests passing — including full simulator and 100% coverage — **never** make a profile clinically valid. Only §6 does.
2. Nothing may be labeled clinically valid / certified / production-ready on the basis of parsing or simulated tests alone.
3. Experimental and Capture-only drivers can never enter production; the tooling enforces this.
4. Compatible drivers still require per-site clinical validation before certification for that site.
5. No real patient data anywhere; fixtures are synthetic or irreversibly de-identified.
6. Clinical validity is scoped to model + firmware + workflow + profile + site and does not generalize.
7. Outbound orders require a separate, recorded approval after inbound results are certified.
8. Certification and production mapping changes require dual approval, separate from engineering.
9. Regulatory/quality framework applicability is a legal/quality workstream, not a software checkbox — see OPEN items.

---

## 11. Open items (unresolved — must be closed before dependent gates)

| OPEN item | Blocks | Owner to resolve |
|---|---|---|
| **OPEN:** Responsible clinical / quality approver identity and reporting line. | `validated → certified` gate; §6 sign-off; §8 dual approval. | Product owner (Phase 0 §4.1) |
| **OPEN:** Regulatory / quality framework — whether IEC 62304 / ISO 13485 / IVDR (or another) applies. This document does **not** assert any applies; treat as a **legal/quality workstream**. | Any certified/regulated production claim; §6 preconditions. | Legal/quality workstream (Phase 0 §4.6) |
| **OPEN:** First analyzer identity — exact model, firmware, host-interface revision, licensing. | Scheduling §6; Phase 10. | Product owner / biomedical engineering (Phase 0 §4.3) |
| **OPEN:** Target LIS/HIS profile results are delivered into. | §6 side-by-side comparison; contract profiles. | Integration lead (Phase 0 §4.4) |
| **OPEN:** Coverage thresholds for safety-critical modules (per-module, not a single vanity %). | Quality gate §5; `mapped → validated` gate. | QA/automation + tech lead (Phase 0) |
| **OPEN:** Operational availability target value for go/no-go (§9). | Phase 12 go/no-go. | Product owner |

---

*End of Validation Strategy (Draft, Phase 0). This document is planning-only and authorizes no deployment, cloud provisioning, real-patient use, analyzer control command, or external-account write.*
