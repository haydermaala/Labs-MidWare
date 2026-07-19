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
| [0008](0008-edge-sqlite-persistence.md) | Edge SQLite persistence (rusqlite bundled, reversible migrations) | Accepted |
| [0009](0009-passive-capture-transport.md) | Capture-only transport contract | Accepted |
| [0010](0010-astm-engine-layering.md) | ASTM engine layering and never-panic/fuzz policy | Accepted |
| [0011](0011-driver-signing.md) | Signed, data-first drivers with a verified install lifecycle | Accepted |
| [0012](0012-control-plane-persistence.md) | Control-plane persistence behind a store interface | Accepted |
| [0013](0013-ef-migrations.md) | EF Core migrations own the control-plane schema | Accepted |
| [0014](0014-fleet-lifecycle.md) | Soft fleet lifecycle: deactivate tenants, decommission gateways | Accepted |
