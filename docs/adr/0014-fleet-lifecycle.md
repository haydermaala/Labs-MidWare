# ADR 0014 — Soft fleet lifecycle: deactivate tenants, decommission gateways

- Status: Accepted (Phase 8)
- Date: 2026-07-19

## Context

The control plane could create tenants and enroll gateways but never stand one
down. Operationally that is a gap: the runbooks call for a **lost or compromised
gateway** to be marked offline and have its credentials rotated/revoked, and a
customer offboarding must stop new enrollment. But a safety-critical system must not
lose provenance: our invariant across the platform is **append-only audit, never
silently drop**. A hard `DELETE` of a tenant or gateway would erase enrollment,
config, and audit history — exactly the record needed for incident response and
reconciliation.

## Decision

- **Soft lifecycle, not deletion.** Tenants and gateways carry an `Active` flag.
  Standing one down flips the flag; the row and all audit/config history are
  retained. There is no hard-delete endpoint.
- **Tenant deactivate / reactivate.** A deactivated tenant cannot issue enrollment
  tokens, and a token issued before deactivation can no longer be redeemed
  (re-checked at `Enroll`). Reactivation restores enrollment. Both are audited
  (`tenant.deactivated` / `tenant.reactivated`).
- **Gateway decommission is credential revocation.** Decommissioning marks the
  gateway inactive **and deletes its device credential**, so it can no longer
  authenticate or fetch config — aligning with the runbook response to a
  lost/compromised gateway. It is deliberately **not reversible**: a returning device
  must re-enroll for a fresh credential (a revoked secret is never un-revoked).
  Audited as `gateway.decommissioned`.
- **Tenant-scoped and admin-only.** Every operation is guarded by the admin bearer
  token and scoped to the tenant in the route; one tenant can never act on another's
  gateway. Decommissioned/inactive entities remain **listed** (with `Active=false`)
  so operators retain fleet visibility.
- **Schema via migration.** `AddLifecycleState` adds the `Active` columns, backfilling
  existing rows to `true` (they predate the feature and were active); new rows always
  set `Active` explicitly.

## Consequences

- Operators can offboard tenants and retire/quarantine gateways without destroying
  the audit trail.
- Credential revocation on decommission gives an immediate, enforced response to a
  compromised edge device.
- **OPEN:** a genuine data-retention/erasure policy (e.g. regulatory "right to
  erasure" vs. clinical record-keeping duties) is a governance decision, tracked with
  the compliance owner; soft state is the safe default until then. Automatic
  actions on a decommissioned gateway's queued/outbound data are out of scope here.

## Alternatives considered

- **Hard delete with cascade**: rejected — destroys provenance and audit; violates
  the platform's append-only invariant and incident-response needs.
- **Reversible gateway disable (keep the credential)**: rejected for the compromise
  case — a disabled-but-valid credential is still a live secret. Revoke-and-re-enroll
  is the safe posture; a benign pause can be modelled later as a separate state if a
  real need appears.
