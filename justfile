# lab-connect task runner. `scripts/verify.sh` is the canonical, just-free entrypoint.
set shell := ["bash", "-uc"]

# Run the full local verification suite (rust + dotnet + js).
verify:
    ./scripts/verify.sh

# Include the slow Tauri desktop compile-check.
verify-full:
    ./scripts/verify.sh --full

# Format everything.
fmt:
    cargo fmt --all
    pnpm -r --if-present run format

# Rust only.
rust-test:
    cargo test --workspace
rust-lint:
    cargo clippy --workspace --all-targets -- -D warnings

# .NET only.
dotnet-test:
    DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" dotnet test LabConnect.slnx -c Release

# JS/TS only.
js-build:
    pnpm -r build
