# Environment Strategy — lab-connect

Status: Draft (Phase 0)
Date: 2026-07-18

> Companion document: [`risk-register.md`](./risk-register.md).
> Authority: `DEVELOPMENT_PLAN.md` §7 (Environment and deployment strategy), §8 (Operations and observability), and `README.md` (Safety boundary).

---

## 0. Scope and standing constraints

This document describes the **intended** environment ladder for the laboratory analyzer middleware ("lab-connect"). It is a plan, not a record of provisioned infrastructure.

**No external infrastructure exists yet.** As of this draft there is no GitHub repository, no Railway project, no Cloudflare account, no R2 bucket, no staging deployment, and no production. Nothing in this document should be read as a claim that any hosted resource has been created, budgeted, or approved.

**Every external resource requires explicit, resource-named authorization** before it is created or mutated. "Named authorization" means a human owner approves *this specific resource* (for example: "create Railway project `lab-connect-dev`", "create R2 bucket `lab-connect-fixtures-dev`") — not a blanket "set up the cloud." Browser access, an existing login, or general project approval is **not** authorization to create or change an external account. Claude Code must request confirmation before any external write.

**No regulatory or compliance claims are made here.** Whether a given environment or hosting choice is acceptable for a jurisdiction, contract, or healthcare regime is a **separate legal/quality workstream** (see §6). This document does not assert fitness for any regulated use.

**No real patient data, ever, in any environment described here.** Repository and CI data are synthetic. Staging uses synthetic or irreversibly de-identified validation data. Production handling of identifiable clinical data is gated on the governance review in §6 and is out of scope for this draft.

---

## 1. The environment ladder

```text
Local development   →   Development cloud   →   Staging   →   Production
(dev machines)          (Railway, ephemeral)    (prod-like)   (authorized only)
   synthetic               synthetic              synthetic/     real clinical data
                                                  de-identified   (governance-gated)
```

Promotion is **one rung at a time**, left to right. Each rung up that touches external infrastructure requires named authorization. Promotion into staging and production is **manual** — never automatic.

| Property | Local | Development cloud | Staging | Production |
|---|---|---|---|---|
| Exists today | Yes (dev machines) | **No** — requires auth | **No** — requires auth | **No** — requires review + auth |
| Data | Synthetic | Synthetic | Synthetic / de-identified | Real clinical (governance-gated) |
| Edge gateway hosted here | Local dev instance | **Never** (runs onsite) | **Never** (runs onsite) | **Never** (runs onsite) |
| Deploy trigger | Manual, local | Auto from `dev` after CI (when authorized) | Manual promotion | Manual, explicitly authorized |
| Accounts / creds / buckets | N/A | Disposable, isolated | Isolated per environment | Fully separate + on-call |
| Backups / restore drills | N/A | Not required | Required + tested | Required + tested |

---

## 2. Local development

Runs entirely on developer machines; no external account required.

### Platforms
- macOS and Windows developer machines. Windows is the first certified production gateway target; macOS is supported per-analyzer.

### Pinned toolchain
Pinned versions are the source of truth and must be reproduced identically in CI (see `risk-register.md`, risk ENV-3). Version files (`rust-toolchain.toml`, `global.json`, `.nvmrc` / `package.json` `packageManager`, etc.) live in the repository.

| Tool | Pinned version |
|---|---|
| Rust | 1.97.1 |
| .NET SDK | 10.0.302 |
| Node.js | 22.17.x |
| pnpm | 10.26.x |

> **OPEN:** Exact patch pin strategy for Node (`22.17.x`) and pnpm (`10.26.x`) — whether to pin to an exact patch or allow the stated patch range — is unresolved. Confirm before Phase 1 scaffolding so CI and dev machines match exactly.

### Data path (default)
- **Default path:** SQLite + the analyzer simulator. This is the primary local development and test path; a synthetic ASTM flow through the gateway needs no external services.
- **When needed:** Docker PostgreSQL (and an optional lightweight broker) for control-plane work that requires the real database engine.

### Secrets and configuration
- `.env.example` contains **variable names and safe, non-secret defaults only**. It is committed.
- Real secret values are **never** committed — not in source, fixtures, screenshots, logs, or diagnostic bundles. They live in an **approved secret store**, not the repository.
- **OPEN:** Which secret store the team standardizes on (per-developer OS keychain, a shared managed secret manager, or per-environment vaults) is unresolved. Owner: OPEN.

---

## 3. Development cloud (Railway)

Intended for low-cost, ephemeral cloud iteration of the control-plane API, web app, and worker. **Does not exist yet; requires named authorization to create.**

- **Cost/lifecycle:** ephemeral, low-cost, disposable. Treat every resource as throwaway.
- **Data:** synthetic only.
- **Database:** disposable PostgreSQL instance — no data of value, safe to destroy and recreate.
- **Object storage:** an R2 **development** bucket (e.g. `lab-connect-fixtures-dev`) — separate from any other environment's bucket.
- **Deploy trigger:** automatic deploys are allowed **only** from the `dev` branch/environment, **only after CI passes**, and **only when the Railway project has been explicitly authorized and created**. Until then, there is nothing to deploy to.
- Define services as code, with health checks, migrations run as controlled jobs, private networking, and resource/budget alerts.

> The edge gateway is **never** deployed here — see §7.

---

## 4. Staging

A production-like rung for validation and rollback rehearsal. **Does not exist yet; requires named authorization to create.**

- **Topology:** production-like — mirrors the intended production shape closely enough to exercise deploys, migrations, and rollback.
- **Isolation:** its own isolated accounts, projects, databases, and buckets. No credential, bucket, or project is shared with development cloud or production.
- **Data:** synthetic or irreversibly **de-identified** validation data only. No real patient data.
- **Promotion:** **manual** promotion from an approved commit. No automatic promotion into staging.
- **Resilience drills (required and exercised):**
  - Backups configured and running.
  - **Restore tests** — periodically restore from backup and confirm integrity.
  - **Rollback** — deploy-and-roll-back rehearsed so the procedure is known-good before production exists.
- Monitoring and alerting active (see §8).

---

## 5. Production

**Does not exist and must not be created in Phase 0.** Production is created **only after** (a) the compliance/hosting review in §6 completes and (b) **explicit, resource-named authorization** is granted for each production resource.

When authorized, production is fully separated from every lower rung:

- Separate **credentials** and service accounts.
- Separate **domains** (DNS/custom-domain changes require an explicit change window).
- Separate **database**.
- Separate **R2 bucket** (e.g. distinct production bucket, never reusing a dev/staging bucket).
- Separate **signing keys**.
- Separate **monitoring** stack.
- Separate **budgets** and cost alerts.
- Dedicated **on-call** process and support-access audit.

### Edge specifics
- The edge gateway (`gatewayd`) runs **onsite** at the laboratory, not in any cloud (see §7).
- Edge releases use **stable and canary** channels; canary reaches a small cohort of gateways first.
- The edge **remains fully functional offline** — it continues to receive, persist, parse, normalize, queue, and store-and-forward when the control plane or internet is unavailable.

> **OPEN:** Production hosting provider(s), region(s)/data-residency, and whether Railway is suitable for any production component are all unresolved and blocked on the §6 review. Owner: OPEN.

---

## 6. Compliance and hosting review (separate workstream)

Production readiness depends on a **dedicated legal/quality/security workstream**, separate from engineering delivery. This document makes **no** regulatory or compliance determination. That review — covering data residency, contractual obligations, security posture, availability commitments, and healthcare governance — must complete and explicitly authorize production before any production resource is created or any identifiable clinical data is stored anywhere (including R2).

> **OPEN:** Owners, scope, and completion criteria for the compliance/hosting review. Owner: OPEN.

---

## 7. The edge gateway is never hosted in the cloud

The edge service (`gatewayd`) is **never** deployed on Railway or any other cloud environment described in this ladder. It **runs onsite** at the laboratory, adjacent to the analyzers it connects to, because:

- It must keep operating when the control plane, network, or internet is unavailable (durable store-and-forward).
- It talks to physical transports (serial / USB virtual COM / onsite TCP / watched files) that exist only on the lab network.
- The analyzer VLAN is untrusted input and stays isolated onsite.

The cloud rungs host only the **control plane** (API, web app, worker, database, object storage). The gateway connects **outbound** to the control plane over a mutually authenticated channel; no inbound port into the lab is required at most sites.

---

## 8. Observability

Operational telemetry is kept **strictly separate** from clinical/result payloads.

### Signals to track
- Connection state (per device/transport)
- Last byte received / last message received (timestamps)
- Parser errors
- Checksum failures
- Queue depth and queue age
- Delivery latency
- Retries
- Duplicates suppressed
- Dead letters
- Certificate expiry
- Disk space / disk pressure
- Version drift (gateway, driver, config versions vs. expected)
- Update status

### Hard rule for telemetry
**Result values and patient identifiers must never appear in metric labels, metric names, tags, or log dimensions.** Metrics describe operational health only. Any field that could carry clinical content or a patient identifier is excluded from telemetry cardinality and redacted from logs, crash reports, screenshots, and support bundles.

---

## 9. Runbooks to author

Author operational runbooks for each of the following. These are the **topics**; the runbooks themselves are separate deliverables (target: `docs/operations/runbooks/`).

| Runbook | Trigger it addresses |
|---|---|
| Analyzer offline | Device stops sending; connection lost |
| Port locked | Serial/COM port held or exclusive-access failure |
| Checksum storm | Sustained burst of checksum failures |
| Mapping unresolved | Incoming data has no approved mapping |
| LIS unavailable | Downstream LIS/HIS not accepting deliveries |
| Queue growth | Outbox depth/age climbing beyond threshold |
| Disk pressure | Low disk on the gateway host |
| Expired certificate | Gateway/device cert expired or near expiry |
| Failed update | Gateway or driver update failed / needs rollback |
| Suspected compromise | Security incident on gateway or channel |
| Lost gateway | Gateway host unreachable, stolen, or destroyed |
| Driver rollback | Roll a driver back to a prior verified version |

> **OPEN:** Runbook authors and review owners. Owner: OPEN.

---

## 10. Summary of unresolved items

- **OPEN:** Exact Node/pnpm patch pin strategy (§2).
- **OPEN:** Approved secret-store choice (§2).
- **OPEN:** Production provider, region/data-residency, Railway production suitability (§5).
- **OPEN:** Compliance/hosting review owners, scope, completion criteria (§6).
- **OPEN:** Runbook authors and review owners (§9).

No external resource named in this document is assumed to exist. Each requires explicit, resource-named authorization before creation or mutation.
