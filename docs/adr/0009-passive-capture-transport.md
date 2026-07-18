# ADR 0009 — Capture-only transport contract

- Status: Accepted (Phase 3)
- Date: 2026-07-18

## Context

Analyzer bytes are untrusted, and an unreviewed device must never be transmitted
to (safety boundary; unknown devices are capture-only). The transport layer must
make it structurally impossible to send to a device by accident, must bound all
buffers, and must be testable in CI without hardware.

## Decision

Model transports as **passive capture only**. `transport-core` exposes:

- `capture_reader()` — reads from any `std::io::Read` into bounded `Captured`
  chunks (each ≤ `max_frame_bytes`) and pushes them into a **bounded**
  `CaptureSink` (a `sync_channel`, so a full consumer applies real backpressure
  rather than unbounded buffering).
- `TransportStats` — numeric-only counters (no PHI/payloads) safe for metrics.
- `BackoffPolicy` — deterministic, testable exponential backoff with jitter
  supplied as a unit value (production passes a random unit; tests pass fixed).
- `PassiveTransport` — a trait with a `capture()` entry point and a source label
  and **no send/write method at all**.

"Capture-only" is therefore a **compile-time** property: there is no API on the
capture path to write back to a device. Outbound-to-device is a separate,
capability-gated concern implemented elsewhere and disabled by default.

Concrete transports (serial/TCP/file) implement `PassiveTransport` by supplying a
`Read` source; the shared capture/bounds/stats logic lives once in
`transport-core`. Initial implementations are synchronous (std + threads +
bounded channels) — an async runtime is not introduced until a demonstrated
workflow needs it.

## Consequences

- A whole class of "accidentally transmitted to the analyzer" bugs is impossible
  by construction.
- Bounds (frame size, channel capacity) are explicit and enforced; oversized
  reads are segmented, never a single unbounded allocation.
- The capture core is fully unit-testable with in-memory readers (no hardware).
- Synchronous threads are fine for a handful of onsite devices; revisit with a
  superseding ADR if connection counts demand async.

## Alternatives considered

- **A single `Transport` trait with `send()` guarded by a runtime flag**: rejected
  — a runtime check can be bypassed or mis-set; the type system is stronger.
- **Async (tokio) from the start**: deferred — unnecessary infrastructure for
  passive capture of a few devices; adds build/complexity now.
