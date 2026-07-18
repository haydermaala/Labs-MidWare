# ADR 0008 — Edge SQLite persistence (rusqlite bundled, reversible migrations)

- Status: Accepted (Phase 2)
- Date: 2026-07-18

## Context

The edge gateway must persist durably and locally — raw messages, normalized
result sets, a delivery outbox, acknowledgements, dead-letters, dedup keys, audit,
and config — and keep working with no network (ADR 0005 chose SQLite). It needs:
reproducible builds across macOS/Windows/CI, foreign-key-enforced provenance, and
migrations that can be **rolled back** (a Phase 2 acceptance criterion).

## Decision

Use `rusqlite` with the **`bundled`** feature so SQLite is compiled in — no
dependency on a system `libsqlite3`, which keeps Windows/macOS/CI builds
reproducible. Enforce `PRAGMA foreign_keys = ON` and use WAL journaling on
file-backed databases.

Use a small, dependency-free migration runner keyed on `PRAGMA user_version`, with
an explicit `up` and `down` SQL pair per migration, so upgrade and rollback are
both tested. Migrations are append-only (never edit a released migration).

Raw messages are treated as **append-only evidence**: the only sanctioned mutation
is audited retention pruning. Every `result_sets` row has a `NOT NULL` foreign key
to its `raw_messages` row, so a normalized result can never exist without raw
provenance. The outbox deduplicates on a `UNIQUE(dedup_key)`.

Synchronous access is used for now; an async runtime is not introduced until a
demonstrated workflow needs it (per the plan's "no unnecessary infrastructure").

## Consequences

- No system SQLite dependency; single self-contained binary per OS.
- Rollback is first-class and covered by `migrations_upgrade_and_rollback`.
- Provenance is enforced at the database level, not just in code.
- At-rest encryption is recorded per raw message (`encryption` column) but not yet
  implemented — the scheme is **OPEN** (see `docs/security/data-classification.md`).
- Synchronous DB calls will need care once the daemon becomes async (likely a
  dedicated DB thread or `spawn_blocking`); revisit via a superseding ADR then.

## Alternatives considered

- **sqlx (async, compile-time-checked)**: attractive, but pulls in an async
  runtime before we need one and complicates the bundled-SQLite reproducibility
  story. Reconsider when the daemon goes async.
- **rusqlite_migration crate**: fine, but a ~50-line runner gives full control over
  the down path and avoids a dependency; revisit if migration needs grow.
- **System libsqlite3**: rejected — non-reproducible across Windows/macOS/CI.
