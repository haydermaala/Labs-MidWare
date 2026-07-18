# ADR 0006 — Simulator-first development

- Status: Accepted (Phase 0)
- Date: 2026-07-18

## Context

Physical analyzers, licensed interface manuals, and controlled lab time are
scarce and safety-sensitive. Connecting to real devices without authorization and
a safe procedure is prohibited. Yet protocol engines, transports, and the vertical
slice need continuous, deterministic testing.

## Decision

Develop against a synthetic analyzer simulator (`services/simulator`) first. The
first milestone is a fully simulated vertical slice: simulated ASTM analyzer →
gatewayd → normalized result → local API → technician UI → audit trail. The
simulator drives normal, multi-frame, malformed, lost-ACK, NAK/retry, duplicate,
timeout, disconnect, and query scenarios, and runs in CI. Physical-analyzer
integration is a later, separately-authorized phase with passive capture first.

## Consequences

- Deterministic, CI-friendly development independent of hardware availability.
- No clinical validity is implied by simulator success — that requires the
  controlled physical-analyzer validation procedure (see validation-strategy).
- Simulator data is synthetic by construction; it must never carry real patient
  data.

## Alternatives considered

- **Hardware-first**: blocked on device/manual access and unsafe without a
  validated procedure — rejected.
