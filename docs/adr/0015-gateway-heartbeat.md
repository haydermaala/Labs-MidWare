# ADR 0015 — Gateway heartbeat and derived liveness

- Status: Accepted (Phase 8)
- Date: 2026-07-19

## Context

The fleet view showed which gateways exist and whether they are active, but not
whether they are actually *reachable*. The runbooks depend on knowing a gateway is
offline ("heartbeat gone → mark offline in fleet, investigate site"), and an
operator needs to distinguish a healthy edge from a silent one. The edge is
capture-only and needs no inbound port, so the signal must be **gateway-initiated**.

## Decision

- **Gateway-initiated heartbeat.** `POST /api/gateways/heartbeat`, authenticated by
  the device credential (same mechanism as config fetch), records the gateway's
  last-seen time. A successful **config fetch also counts as a heartbeat** — any
  authenticated contact is a liveness signal.
- **Liveness is derived, not stored.** Only `LastSeenAt` is persisted. The
  online/offline label is computed at read time from `LastSeenAt` against a staleness
  window (`GatewayLiveness`, currently 2 minutes), so it is always correct for "now"
  without a background job writing status. `Status` ∈ `never` (never seen),
  `online`, `offline`, `decommissioned` (an inactive gateway is never "online").
- **Revocation still wins.** A decommissioned gateway has no credential, so it cannot
  heartbeat (401) and shows `decommissioned` — consistent with [ADR 0014](0014-fleet-lifecycle.md).
- **No PHI, redaction-safe.** The heartbeat carries no payload; only a timestamp is
  stored and only numeric/enumerated liveness is exposed.

## Consequences

- Operators can see per-gateway last-seen and online/offline in the tenant fleet
  view, satisfying the "heartbeat gone" runbook.
- No background sweeper or clock-writes; status can never be stale-in-storage because
  it is computed on read.
- **OPEN:** the 2-minute window and the gateway's heartbeat interval are defaults, not
  yet policy; alerting/paging on transitions to `offline`, and persisting a
  liveness history for SLA reporting, are future work (observability owner).

## Alternatives considered

- **Persist a status column updated by a background job**: rejected — adds a sweeper,
  and a crashed sweeper leaves a lying `online`. Deriving on read is simpler and
  always correct.
- **Server-push / long-lived connection health**: rejected — the edge is capture-only
  and must not depend on inbound connectivity; a periodic gateway-initiated ping fits
  the store-and-forward posture.
