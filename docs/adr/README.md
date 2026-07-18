# Architecture Decision Records

ADRs record material architecture decisions and the reasoning behind them. Any
change to the fixed stack requires a new ADR (superseding the relevant one)
*before* the change lands. Format: MADR-lite (Context / Decision / Consequences /
Alternatives).

| ADR | Title | Status |
|-----|-------|--------|
| [0001](0001-monorepo.md) | Monorepo with Rust + pnpm + .NET workspaces | Accepted |
| [0002](0002-rust-edge.md) | Rust for the edge gateway | Accepted |
| [0003](0003-tauri-desktop.md) | Tauri 2 + React for the technician desktop | Accepted |
| [0004](0004-aspnetcore-control-plane.md) | ASP.NET Core on .NET 10 LTS control plane | Accepted |
| [0005](0005-sqlite-postgres.md) | SQLite at the edge, PostgreSQL centrally | Accepted |
| [0006](0006-simulator-first.md) | Simulator-first development | Accepted |
| [0007](0007-exact-decimal.md) | Exact decimals for clinical numeric values (rust_decimal) | Accepted |
