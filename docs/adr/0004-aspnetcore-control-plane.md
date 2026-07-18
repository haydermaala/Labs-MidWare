# ADR 0004 — ASP.NET Core on .NET 10 LTS for the control plane

- Status: Accepted (Phase 0)
- Date: 2026-07-18

## Context

The control plane provides multi-tenant fleet management, driver registry,
mapping approvals, audit, and gateway enrollment. It needs a mature, supported,
long-term backend platform with strong tooling and a clear support horizon.

## Decision

Build the control-plane backend as an ASP.NET Core modular monolith on
**.NET 10 (LTS)**, SDK pinned to **10.0.302** via `global.json`
(`rollForward: latestFeature`). Verified against the official .NET support policy:
.NET 10 is LTS, released 2025-11-11, supported through 2028-11-14. Start as a
modular monolith; extract services only when measured need demonstrates it.

Shared project defaults (`Directory.Build.props`): nullable enabled, warnings as
errors, .NET analyzers, deterministic + invariant-globalization builds.

## Consequences

- 3-year support runway; first-class OpenAPI, auth (OIDC), EF Core, and testing.
- Team maintains a .NET toolchain alongside Rust and Node.
- SDK pin bumps require updating `global.json` and CI; roll-forward limited to the
  feature band.

## Alternatives considered

- **.NET 8 (LTS)**: supported only to 2026-11-10 — too short for a new product.
- **.NET 9 (STS)**: 2-year horizon, not LTS — rejected.
- **Node/NestJS or Go backend**: viable, but the plan fixes ASP.NET Core; no
  evidence to justify substituting the fixed stack.
