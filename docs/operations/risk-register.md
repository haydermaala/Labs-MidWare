# Risk Register — lab-connect

Status: Draft (Phase 0)
Date: 2026-07-18

> Companion document: [`environment-strategy.md`](./environment-strategy.md).
> Authority: `DEVELOPMENT_PLAN.md` §11 (Principal risks and mitigations), plus environment/tooling risks discovered during Phase 0 scaffolding.

---

## How to read this register

- **All owners are `OPEN`.** Named accountable owners are assigned in Phase 0 (product owner, technical lead, clinical/laboratory safety owner, security owner, release approver). Until then every risk is unowned and that is itself a tracked gap — see risk GOV-1.
- Unresolved decisions are marked **OPEN:** inline.
- **Likelihood** and **Impact** are coarse planning estimates (Low / Medium / High), not measured probabilities. They will be recalibrated after Phase 0.
- This register makes **no regulatory or compliance claims.** Compliance/regulatory certification is a **separate legal/quality workstream** (see risk CLD-1 and `environment-strategy.md` §6), not a software checkbox.
- **No real patient data** appears anywhere in the project; risks below assume synthetic/de-identified data in all pre-production environments.

---

## Register

| ID | Risk | Category | Likelihood | Impact | Mitigation | Owner |
|---|---|---|---|---|---|---|
| CLIN-1 | **Unavailable / proprietary manuals** — host-interface documentation for a target analyzer is unavailable, licensed-only, or confidential, blocking a correct driver. | Clinical / Vendor | High | High | Begin with the simulator and legally obtained documentation only; never reverse-engineer beyond authorization; confirm interface licensing before physical integration (Phase 10). Do not commit any protocol manual or vendor document to the repo. | OPEN |
| CLIN-2 | **Clinical semantic error** — a mapping or transformation silently produces a wrong clinical meaning (wrong unit, test, flag, or value). | Clinical / Safety | Medium | High (patient safety) | No auto-activation of inferred mappings/drivers; dual review (mapping reviewer + clinical approver); golden fixtures with expected canonical output; passive capture until a profile is validated and signed; side-by-side comparison with the existing validated workflow before sign-off. | OPEN |
| PLAT-1 | **OS / vendor incompatibility** — vendor drivers or middleware are Windows-only, or OS/firmware differences break connectivity on a target. | Platform | High | Medium | Certify per analyzer/firmware/OS combination; Windows-first for production gateways; macOS supported per-analyzer only; platform test matrix (port naming, permissions, service lifecycle, suspend/resume, installer/update) on both OS targets. | OPEN |
| MSG-1 | **Message loss or duplication** — a result is lost, or delivered more than once, across a fault (crash, network partition, dropped ACK, restart). | Reliability / Safety | Medium | High | Persist-before-process; durable outbox with store-and-forward; idempotency keys and deduplication; delivery receipts, dead-letter review, and acknowledgement reconciliation; fault-injection tests (dropped ACK, corrupted frame, disk full, clock shift, restart, partition, cert expiry). | OPEN |
| DEV-1 | **Unsafe device command** — the middleware sends an unintended or unsafe command to an analyzer. | Safety / Security | Low | High | Passive capture is the default; capability allowlist per driver; capture-only mode physically cannot transmit; outbound orders/commands enabled only after inbound results are fully validated and a **separate** approval is recorded. | OPEN |
| SCOPE-1 | **Scope explosion** — the product drifts toward "all analyzers" / dynamic discovery / AI inference before the core slice is proven. | Delivery / Governance | High | Medium | One simulator-first vertical slice and one physical analyzer before any dynamic discovery or AI; explicit MVP non-goals; phase gates; change-control process; planning estimates treated as ranges, not commitments. | OPEN |
| CLD-1 | **Cloud compliance mismatch** — a hosting choice (Railway / Cloudflare / R2) is unsuitable for data residency, contractual, security, availability, or healthcare-governance needs. | Compliance / Cloud | Medium | High | Staging-only until a **separate** legal/security/data-residency review completes; production created only after that review + explicit resource-named authorization; no identifiable clinical data in R2 until data governance approves. This register asserts **no** compliance determination. | OPEN |
| AGENT-1 | **Agent overreach** — an automated agent (e.g. Claude Code) makes an unauthorized external write, provisions infrastructure, or touches production. | Governance / Security | Medium | High | Phase gates; explicit, resource-named external-write approvals; PR-based changes with required review; no production credentials issued to agents; browser login ≠ authorization; humans own clinical semantics, external-account authorization, and production approval. | OPEN |
| ENV-1 | **Repository lives inside a Google Drive-synced folder** — the working tree is under `/Users/haydermaala/My Drive/TTECH/Labs-MidWare/`. Continuous Drive sync of `target/`, `node_modules/`, and `bin/obj/` risks build corruption, partial writes, and file-lock conflicts during compiles. | Tooling / Environment | High | Medium | `.gitignore` excludes build outputs (`target/`, `node_modules/`, `bin/`, `obj/`, etc.); the team should **exclude those folders from Drive sync**, or **move the working tree to a local, non-synced path**. Verify clean build on a non-synced path during Phase 1 setup. **OPEN:** decision between "exclude-from-sync" vs. "relocate working tree." | OPEN |
| ENV-2 | **Path contains spaces** — the repository path (`My Drive`, `TTECH`, `Labs-MidWare`) contains spaces, which can break scripts, tool invocations, and toolchains that mishandle unquoted paths. | Tooling / Environment | Medium | Medium | Quote all paths in scripts and task runners; test the toolchain end-to-end from this path (or a non-synced equivalent) during Phase 1; prefer relocating to a space-free, non-synced path if any tool proves incompatible. | OPEN |
| ENV-3 | **Toolchains installed to the user profile** — Rust (`~/.cargo`) and .NET (`~/.dotnet`), plus Node/pnpm, live in the developer's home profile. CI must reproduce these **exact** pinned versions or local and CI behavior diverge. | Tooling / Reproducibility | Medium | Medium | Pin versions via committed version files (`rust-toolchain.toml`, `global.json`, Node/pnpm pins) and reproduce them identically in CI; do not rely on machine-global installs; verify clean setup on one Windows and one macOS machine (Phase 1). Pins: Rust 1.97.1, .NET 10.0.302, Node 22.17.x, pnpm 10.26.x. | OPEN |
| GOV-1 | **Accountable owners not yet assigned** — every risk owner in this register is `OPEN`, so no individual is currently accountable for mitigation. | Governance | High | Medium | Assign product owner, technical lead, clinical/laboratory safety owner, security owner, and release approver in Phase 0 (per `DEVELOPMENT_PLAN.md` §9); replace each `OPEN` owner with a named person and revisit likelihood/impact. | OPEN |

---

## How this register is maintained

- **Cadence:** reviewed every two-week iteration as part of the risk review, and whenever a phase gate is crossed.
- **Ownership:** each risk gets a single named accountable owner during Phase 0; `OPEN` is a tracked gap (GOV-1), not a resting state.
- **Changes:** add, re-score, or retire risks via pull request so history is auditable; do not edit silently. New risks discovered during scaffolding or integration are appended with a new ID.
- **Recalibration:** likelihood/impact estimates are coarse and provisional; recalibrate after Phase 0 and after each physical-analyzer integration.
- **Boundaries:** this register records engineering and operational risk. It does **not** make regulatory/compliance determinations — those belong to the separate legal/quality workstream — and it assumes synthetic or de-identified data in all pre-production environments, with no real patient data anywhere.
