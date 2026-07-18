# Contributing to lab-connect

## Prerequisites

| Tool | Version (pinned) | Notes |
|------|------------------|-------|
| Rust | 1.97.1 (`rust-toolchain.toml`) | `rustup` auto-selects the pinned channel |
| .NET SDK | 10.0.302 (`global.json`) | LTS; ASP.NET Core 10 runtime |
| Node.js | 22.17.x (`.nvmrc`) | LTS |
| pnpm | 10.26.x (`packageManager`) | via corepack |

macOS also needs Xcode Command Line Tools. The Tauri desktop shell builds against
system WebKit on macOS; on Windows it needs the WebView2 runtime and MSVC build tools.

## One-command verification

```bash
scripts/verify.sh          # rust + dotnet + js
scripts/verify.sh --full   # also compile-checks the Tauri desktop shell (slow)
```

(Or `just verify` if you have [`just`](https://github.com/casey/just).)

## Repository layout

Monorepo with a Rust workspace (`crates/`, `services/`), a pnpm workspace
(`apps/`, `packages/`), and a .NET solution (`LabConnect.slnx`). See
`DEVELOPMENT_PLAN.md` §3 and `docs/architecture/` for the component model.

The Tauri `src-tauri` project is intentionally a **separate** Cargo workspace and
is excluded from the root workspace.

## Engineering rules

- **Trunk-based**: short feature branches, protected `main`, PR review required.
- **Conventional Commits**: `feat:`, `fix:`, `docs:`, `chore:`, `refactor:`, `test:`.
- **ADRs before material change**: record architecture decisions in `docs/adr/`
  before substituting any part of the fixed stack.
- **Small PRs**: never combine unrelated refactors with feature work.
- **Synthetic data only**: no real patient data or confidential vendor manuals
  in the repo, ever.
- **No clinical claims from parsing/simulation alone.** A profile is not
  clinically valid until controlled expected/actual validation is signed off.
- **Every PR is green**: format, lint, typecheck, unit/integration tests, and
  secret/dependency/license scans must pass. Parser code needs property/fuzz
  coverage as it lands.

## Safety boundaries for automated contributors

Automated agents may implement scoped issues but must not: create or modify
external accounts/resources (GitHub, Railway, Cloudflare, DNS, billing),
transmit to a physical analyzer, use real patient data, or make production
changes — without a specific, named human approval. Humans own clinical
semantics, external-account authorization, and production approval.
