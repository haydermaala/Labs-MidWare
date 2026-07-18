#!/usr/bin/env bash
# Canonical local verification suite for lab-connect.
# One command runs format/lint/typecheck/build/test across Rust, .NET, and TS.
#
# Usage:
#   scripts/verify.sh          # rust + dotnet + js
#   scripts/verify.sh --full   # also compile-checks the Tauri desktop shell (slow, cold)
#
# Exits non-zero on the first failing stage.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

FULL=0
[[ "${1:-}" == "--full" ]] && FULL=1

# --- Toolchain discovery (works even if PATH is not pre-configured) ---
[[ -f "$HOME/.cargo/env" ]] && source "$HOME/.cargo/env"
if [[ -d "$HOME/.dotnet" ]]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$HOME/.dotnet:$PATH"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

section() { printf '\n\033[1;34m==> %s\033[0m\n' "$1"; }

section "Rust: fmt check"
cargo fmt --all -- --check

section "Rust: clippy (warnings as errors)"
cargo clippy --workspace --all-targets -- -D warnings

section "Rust: build + test"
cargo test --workspace

section ".NET: build (Release) + test"
dotnet test LabConnect.slnx -c Release

section "TS/JS: build (typecheck + bundle)"
pnpm install --frozen-lockfile
pnpm -r build

if [[ "$FULL" -eq 1 ]]; then
  section "Tauri: desktop shell compile-check (slow)"
  ( cd apps/gateway-desktop/src-tauri && cargo check )
fi

printf '\n\033[1;32mAll verification stages passed.\033[0m\n'
