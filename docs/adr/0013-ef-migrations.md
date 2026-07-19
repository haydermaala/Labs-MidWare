# ADR 0013 — EF Core migrations own the control-plane schema

- Status: Accepted (Phase 8)
- Date: 2026-07-19

## Context

ADR 0012 shipped the EF Core + PostgreSQL control-plane store and bootstrapped the
schema at startup with `EnsureCreated`. `EnsureCreated` is a dead end for a system
that must evolve safely: it creates the schema only when absent, never applies
incremental changes, refuses to coexist with migrations, and leaves no auditable
record of what shape the database is in. A safety-critical, regulated system needs
schema changes that are versioned, reviewable, reversible, and traceable.

## Decision

- **Migrations are the single source of truth for the schema.** The model is
  materialized as EF Core migrations under `services/control-plane-api/Migrations`,
  starting with `InitialCreate`. Schema changes ship as new, code-reviewed migration
  files — never as ad-hoc SQL or `EnsureCreated`.
- **Design-time factory.** `AppDbContextDesignFactory` (an
  `IDesignTimeDbContextFactory`) lets the `dotnet ef` tools build the Npgsql model
  without a live database or a running host. It is design-time only; the runtime
  builds its own context from configuration.
- **Apply on startup for staging.** When a Postgres connection is configured the app
  calls `Database.Migrate()` on startup, so a single-replica staging deploy is always
  at the latest schema with no extra step. Tests and local dev use the in-memory
  store and never migrate.
- **Baseline the pre-existing database.** The staging database was first created by
  `EnsureCreated`, so it has the tables but no `__EFMigrationsHistory`. It is
  **baselined** once — the `InitialCreate` id is recorded as already applied — so the
  first `Migrate()` is a no-op instead of trying to recreate existing tables. This is
  a one-time transition, not a recurring step.

## Consequences

- Schema evolution is now versioned, diffable in review, and reversible (`Down`).
- `Relational` is pinned to the EF Core version on the API project (a non-private
  reference) so the design/tooling constraint propagates and the version stays
  unified across the API and its test project.
- **OPEN — production rollout:** applying migrations automatically at startup is not
  acceptable for a multi-replica or regulated production deploy (racing replicas,
  unreviewed data-affecting changes, no go/no-go gate). Production must apply
  migrations as a **gated release step** (e.g. `dotnet ef migrations bundle` or a
  generated idempotent script run under change control) before the new app version
  serves traffic, with backups and a rollback path. This is tracked with the other
  ⛔ owner items in the security/rollout docs.

## Alternatives considered

- **Keep `EnsureCreated`**: rejected — no incremental changes, no history, no
  rollback; unusable once the schema must evolve.
- **Hand-written SQL migrations**: rejected — loses model/schema drift detection and
  the `Down` path; EF migrations are generated from the same model the code uses.
- **Drop and recreate the staging DB**: viable given throwaway data, but baselining
  is the technique a real production cutover requires, so we use it here to exercise
  the correct path.
