# ADR 0005 — SQLite at the edge, PostgreSQL centrally

- Status: Accepted (Phase 0)
- Date: 2026-07-18

## Context

The edge gateway must persist raw messages, outbox, dedup keys, acks, and audit
durably and locally, surviving restart and operating with no network. The control
plane needs a multi-tenant relational store with backups and migrations.

## Decision

Use embedded SQLite (with WAL, append-oriented raw storage, encryption support,
configurable retention) at the edge — zero external dependencies, single-file
durability, offline-first. Use PostgreSQL centrally for the control plane
(tenants, registry, audit) with controlled migrations and backup/restore.

## Consequences

- Edge has no external DB dependency and keeps working offline (persist-before-
  process is local).
- Two SQL dialects to manage; schemas and migrations are maintained separately.
- **OPEN:** at-rest encryption mechanism (SQLCipher vs. app-layer) and central DB
  hosting are decided per environment/threat-model in a later phase.

## Alternatives considered

- **Postgres at the edge**: heavy operational burden onsite — rejected.
- **Embedded KV (sled/RocksDB) at the edge**: loses SQL query/migration ergonomics
  for audit/reconciliation — rejected for now.
