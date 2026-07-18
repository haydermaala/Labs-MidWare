# ADR 0001 — Monorepo with Rust + pnpm + .NET workspaces

- Status: Accepted (Phase 0)
- Date: 2026-07-18

## Context

The product spans a Rust edge daemon, shared Rust protocol/model crates, a Tauri
desktop app, a React web app, shared TS packages, and an ASP.NET Core backend.
These share contracts (canonical model, REST DTOs, driver manifest, validation
schemas) that must stay version-consistent. Split repos would fragment contract
versioning and cross-cutting change review.

## Decision

Use a single monorepo containing a Rust workspace (`crates/`, `services/`), a
pnpm workspace (`apps/`, `packages/`), and a .NET solution (`LabConnect.slnx`).
Contracts are shared via `packages/contracts`, `packages/validation-schemas`,
and the `canonical-model` crate. Trunk-based development with protected `main`,
Conventional Commits, and CI covering all three toolchains.

The Tauri `src-tauri` project is intentionally excluded from the root Cargo
workspace (its own `[workspace]`) so desktop build flags stay isolated.

## Consequences

- One PR can change a contract and all consumers atomically; CI verifies all
  stacks together.
- Requires multi-toolchain CI (Rust/.NET/Node) and clear ownership boundaries
  (`CODEOWNERS`).
- Build-output directories (`target/`, `node_modules/`, `bin/obj/`) must be
  git-ignored and excluded from any folder sync (see risk register: the tree
  currently lives under Google Drive).

## Alternatives considered

- **Polyrepo**: better isolation, worse contract coordination — rejected for a
  small team needing atomic cross-stack changes.
- **Nx/Turbo-managed monorepo**: deferred; plain workspaces + `scripts/verify.sh`
  suffice for Phase 1. **OPEN:** revisit a task runner if build graph grows.
