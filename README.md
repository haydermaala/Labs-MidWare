# Laboratory Analyzer Middleware — Development Package

Version: 1.0 (planning baseline)  
Date: 2026-07-18  
Status: Planning only; no production changes authorized

## Package contents

- `DEVELOPMENT_PLAN.md` — architecture, phased plan, work breakdown, security, testing, deployment, and rollout.
- `CLAUDE_CODE_MASTER_PROMPT.md` — the complete prompt to start a Claude Code development session.
- `OPERATOR_CHECKLIST.md` — preparation and safe handoff checklist for the project owner.
- `Laboratory_Analyzer_Middleware_Development_Package.docx` — formatted compilation for review and sharing.

## Fixed technical direction

Build a hybrid product:

1. A native Rust edge service for reliable device communication.
2. A Tauri 2 + React technician application for Windows and macOS.
3. A web control plane for fleet, drivers, mappings, users, audit, and monitoring.
4. A stable LIS/HIS-facing API supporting REST first, then HL7 v2 and FHIR.

Windows is the first production gateway target. macOS is a supported target for compatible serial, TCP, and file-based analyzers, but compatibility is certified per analyzer because vendor drivers and middleware may be Windows-only.

## Safety boundary

Until a device profile is reviewed and validated, the gateway must operate in passive capture mode. The package does not authorize deployment, cloud provisioning, billing, DNS changes, production secrets, real patient data, analyzer control commands, or writes to any external account.

## Recommended first milestone

Prove one complete vertical slice using a simulator:

`simulated ASTM analyzer → Rust gateway → normalized result → local API → technician UI → audit trail`

After that passes, validate a single real analyzer in an isolated lab environment.

## Local development (Phase 1 scaffold)

The monorepo is scaffolded and compiles on all three stacks. Nothing external is
provisioned; no product features exist yet.

Prerequisites (pinned): Rust 1.97.1 (`rust-toolchain.toml`), .NET SDK 10.0.302
(`global.json`), Node 22.17.x (`.nvmrc`), pnpm 10.26.x. See
[CONTRIBUTING.md](CONTRIBUTING.md).

One-command verification:

```bash
scripts/verify.sh          # Rust + .NET + TypeScript
scripts/verify.sh --full   # also compile-checks the Tauri desktop shell (slow)
```

Layout: Rust workspace (`crates/`, `services/gatewayd`, `services/simulator`),
pnpm workspace (`apps/`, `packages/`), .NET solution (`LabConnect.slnx` →
`services/control-plane-api` with `/health`). Planning docs and ADRs live under
[`docs/`](docs/) (`docs/adr/`, `docs/architecture/`, `docs/security/`,
`docs/validation/`, `docs/operations/`, `docs/product/`).

> Note: this working tree currently lives inside a Google Drive-synced folder.
> Build outputs are git-ignored; exclude `target/`, `node_modules/`, and
> `bin/obj/` from Drive sync (or move the tree to a local non-synced path) to
> avoid build corruption. Tracked in the risk register.
