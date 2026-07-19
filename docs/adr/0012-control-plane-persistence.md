# ADR 0012 — Control-plane persistence behind a store interface

- Status: Accepted (Phase 8)
- Date: 2026-07-19

## Context

The control-plane API (tenants, secure gateway enrollment, config, audit) began
with an in-memory store so the vertical slice could be exercised end-to-end and in
CI without a database. ADR 0005 already committed the platform to PostgreSQL for
the cloud control plane. We now need durable, multi-tenant persistence for staging
and deployment while keeping tests fast, hermetic, and free of native-dependency
CVEs (an earlier attempt via `Microsoft.EntityFrameworkCore.Sqlite` pulled in a
high-severity `SQLitePCLRaw` advisory that fails our warnings-as-errors gate).

## Decision

- **`IControlPlaneStore` interface.** Endpoints depend only on the interface, so
  the persistence backend is a deployment choice, not a code change. Two
  implementations exist:
  - `InMemoryControlPlaneStore` — the default for local development and tests
    (no database, no native deps).
  - `EfControlPlaneStore` — EF Core + Npgsql, selected at startup when a Postgres
    connection is configured.
- **Backend selected by configuration.** `DatabaseConfig.ResolveConnectionString`
  reads `DATABASE_URL` (the `postgres://` URL form managed hosts like Railway
  provide) or `ConnectionStrings:Postgres`, normalizing either to an Npgsql
  key=value string and defaulting `sslmode` to `require`. Absent a connection
  string, the in-memory store is used.
- **Tenant isolation preserved in SQL.** Every gateway/config/audit read is
  filtered by tenant id; the schema keeps tenant ids as indexed foreign keys.
- **Single-use enrollment enforced at the database.** The bootstrap token carries
  an optimistic-concurrency token; redeeming it is a tracked update whose `WHERE`
  clause includes that token, so concurrent redemption makes the loser's
  `SaveChanges` throw `DbUpdateConcurrencyException` — the token is redeemable
  exactly once even under a race, on real Postgres.
- **Schema bootstrap.** `EnsureCreated` builds the schema on startup for this
  increment; EF **migrations** replace it before production.
- **Tests run on the EF in-memory provider** so `EfControlPlaneStore`'s behaviour
  (tenant isolation, single-use tokens, config versioning, audit scoping) is
  asserted without a live database, and the CI dependency-audit gate stays clean.

## Consequences

- Deployments get durable Postgres persistence; CI and local dev stay database-free
  and fast.
- The store contract is the seam for future backends and for OIDC-scoped access
  (still OPEN).
- **OPEN:** replace `EnsureCreated` with migrations; at-rest encryption decision
  per environment (tracked in the security checklist); connection-pool and
  retry/resiliency tuning for the managed database.

## Alternatives considered

- **EF Core against SQLite for tests**: rejected — pulls in `SQLitePCLRaw`, which
  tripped a high-severity advisory under warnings-as-errors. The managed EF
  in-memory provider avoids native deps entirely.
- **Postgres-only (Testcontainers in CI)**: heavier CI, slower feedback, and a
  Docker dependency for every contributor; deferred until integration tests need
  provider-specific behaviour (e.g. migrations, `xmin`).
- **A single store with a provider switch inside it**: rejected — mixes two
  storage models in one class; separate implementations behind the interface are
  clearer and independently testable.
