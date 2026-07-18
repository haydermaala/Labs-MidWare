# ADR 0010 — ASTM engine layering and never-panic/fuzz policy

- Status: Accepted (Phase 4)
- Date: 2026-07-18

## Context

ASTM connectivity mixes three concerns that are often tangled together in vendor
stacks: low-level framing (E1381: STX/ETX/checksum), link-layer control flow
(ENQ/ACK/NAK/EOT, contention, retries, timeouts), and record semantics (E1394:
H/P/O/R/C/Q/L). Tangling them makes correctness hard to test and unsafe on
malformed input. All ASTM bytes are untrusted.

## Decision

Implement the ASTM engine in three **independently-tested layers** within
`protocol-astm`:

1. `framing` — encode/decode a single, already-delimited frame and compute the
   checksum. Pure functions, no state, no link semantics.
2. Link-layer state machine — ENQ/ACK/NAK/EOT, frame sequencing, contention,
   retries, timeouts. Driven by an injected **virtual clock** so timing behavior
   is deterministic in tests. Separate from framing and records.
3. Record parsing — H/P/O/R/C/Q/L into a **lossless** intermediate representation
   that preserves unknown fields/components and the raw record bytes.

Cross-cutting rules for every layer:

- **Never panic on malformed input.** Malformed bytes yield a typed error, never
  a panic or unbounded loop. Enforced by a deterministic **fuzz-smoke test on
  every PR** (a seeded generator feeds arbitrary bytes to each parser); longer
  fuzz campaigns are scheduled separately.
- **Explicit bounds** on frame text and message size; oversized input is rejected.
- **Lossless retention** — the raw frame/record is preserved so a normalized
  result can always be traced back (provenance).

## Consequences

- Each layer is unit-testable in isolation; the state machine can be tested with a
  virtual clock without real sockets or sleeps.
- Golden vectors + property tests (build→parse roundtrip) + fuzz-smoke give strong
  coverage of the untrusted-input surface.
- More modules/boundaries than a monolithic parser, by design.

## Alternatives considered

- **One combined parser/state-machine**: rejected — couples timing with byte
  parsing, hard to fuzz and to reason about safety.
- **Third-party ASTM library**: none vetted for our safety/never-panic and
  lossless-retention requirements; revisit via a superseding ADR if one qualifies.
