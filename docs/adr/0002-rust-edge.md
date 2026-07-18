# ADR 0002 — Rust for the edge gateway

- Status: Accepted (Phase 0)
- Date: 2026-07-18

## Context

The edge daemon (`gatewayd`) parses untrusted bytes from analyzers over serial,
TCP, MLLP, and files; it must never crash, hang, or leak memory on malformed
input, must run as a long-lived Windows Service / macOS launchd daemon, and must
continue during cloud/LIS outage. Memory-safety and predictable resource use are
safety properties here.

## Decision

Implement the edge core in Rust (stable, pinned to **1.97.1** via
`rust-toolchain.toml`). Enforce `#![forbid(unsafe_code)]` in crates by default,
`overflow-checks = true` in release, clippy-as-error, and property/fuzz tests for
parsers. Protocol state machines are separated from record parsing per the plan.

## Consequences

- Strong memory safety and bounded-resource control for untrusted-input parsing.
- Single static binary per OS eases service packaging.
- Team must maintain Rust expertise; toolchain pin bumps require an ADR + CI
  matrix update.

## Alternatives considered

- **Go**: simpler concurrency, but weaker guarantees on zero-copy parsing limits
  and no `forbid(unsafe)`-style discipline — rejected.
- **C++**: mature serial/USB libraries but memory-safety burden unacceptable for
  safety-critical parsing — rejected.
