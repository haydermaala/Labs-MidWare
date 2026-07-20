# Laboratory Analyzer Middleware — Development Package

Version: 1.0 (planning baseline)  
Date: 2026-07-18  
Status: Planning only; no production changes authorized

## Package contents

- `DEVELOPMENT_PLAN Procution.md` — architecture, phased plan, work breakdown, security, testing, deployment, and rollout.
- `CLAUDE_CODE_MASTER_PROMPT.md` — the complete prompt to start a Claude Code development session.
- `PRODUCTION_EXECUTION_PLAN.md` — detailed online production, product UI, account, DNS, billing, email, and launch plan.
- `CLAUDE_PRODUCTION_MASTER_PROMPT.md` — production-authorized master prompt preserving `/ui-ux-pro-max` exactly.
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

The production prompt is a separate handoff. It authorizes Claude Code to build and deploy `lc.spottiq.com` within tightly defined account boundaries while retaining the clinical/device safety restrictions.

## Recommended first milestone

Prove one complete vertical slice using a simulator:

`simulated ASTM analyzer → Rust gateway → normalized result → local API → technician UI → audit trail`

After that passes, validate a single real analyzer in an isolated lab environment.
