# Staged production rollout plan

- Status: Draft (Phase 12)
- Date: 2026-07-18

> **Production is created only after compliance/hosting review and explicit,
> resource-named authorization.** This plan describes the *staged path*; it does
> not authorize any production change.

## Stages (each gate must pass before the next)

1. **Internal dogfood** — simulator + synthetic data end-to-end (done in CI: the
   ASTM→canonical→outbox→HL7→mock-LIS loop and the technician UI).
2. **Lab bench validation** — one analyzer, **passive capture first**, no LIS
   writeback. Requires the physical-analyzer procedure (separate approval + safe
   procedure + isolated network).
3. **Shadow mode** — receive and compare against the existing validated workflow;
   **do not release** results. Zero unexplained discrepancies required.
4. **Controlled unidirectional pilot** — release inbound results to production
   LIS with manual reconciliation. Outbound orders remain disabled.
5. **Bidirectional** — only after inbound is fully validated and a **separate**
   approval is recorded; outbound capability is allowlisted.
6. **Cohort expansion** — a small set of sites/models with **canary** gateway and
   driver releases.
7. **Scale** — only after reliability, discrepancy, support, and recovery metrics
   meet thresholds.

## Go / no-go metrics (all must hold to advance)

- Message loss in validated paths: **0**
- Unexplained clinical discrepancies: **0**
- Duplicate result release: **0**
- Queue recovery after outage: **verified**
- Acknowledgement reconciliation: **complete**
- Operational availability target: **met** (target OPEN)
- Support + rollback readiness: **confirmed**

## What software already guarantees

- Passive-capture default (no transmit) enforced at the type level.
- Persist-before-process; provenance enforced by a DB foreign key.
- Results held (`PendingReview`) — nothing releases without a validated decision.
- Idempotent delivery (outbox dedup) — no duplicate release across retries/restarts.
- Driver install verification + rollback + revocation.
- Full audit trail at each step.

## Gates requiring the owner

Physical-analyzer connection, LIS production writeback, code-signing identities,
Railway/Cloudflare provisioning, and any production authorization are **explicit,
resource-named approvals** — none are implied by this plan.
