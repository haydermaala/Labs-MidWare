# Backup & restore — control-plane Postgres

The control plane's system of record is its PostgreSQL database on Railway
(tenants, users, memberships, gateways, subscriptions, audit). Everything else
(the API container, the edge SQLite stores) is reconstructible; this database is
not. This runbook covers backups, the restore **drill**, and disaster recovery.

> A backup you have never restored is not a backup. Run the drill on a schedule
> and before any risky migration or rollout.

## Backups

- **Railway managed backups**: enable scheduled backups on the Postgres service
  in the Railway dashboard (Service → Backups). Confirm the schedule and
  retention meet the recovery-point objective. These are point-in-time snapshots
  managed by Railway.
- **Portable logical backup** (provider-independent, restorable anywhere):
  ```bash
  pg_dump --format=custom --no-owner --no-privileges \
    --file=control-plane-$(date +%Y%m%d).dump "$DATABASE_URL"
  ```
  Store off-Railway (e.g. Cloudflare R2) with restricted, scoped credentials.
  The dump contains tenant data — treat it as sensitive; encrypt at rest.

## Restore drill — `scripts/restore-drill.sh`

Proves a backup restores into a **separate scratch** database and that the
result is intact (schema migrated, data present). It is read-only against the
source and drops/replaces objects in the target.

```bash
SOURCE_DATABASE_URL=postgres://…   # a backup, replica, or the live DB (read-only)
TARGET_DATABASE_URL=postgres://…   # a THROWAWAY database — contents are replaced
scripts/restore-drill.sh
```

If the Postgres client tools are not installed locally, run it inside the
official image:

```bash
docker run --rm -e SOURCE_DATABASE_URL -e TARGET_DATABASE_URL \
  -v "$PWD/scripts:/s" postgres:16 bash /s/restore-drill.sh
```

The drill: `pg_dump` the source → `pg_restore --clean --if-exists` into the
target → verify `__EFMigrationsHistory` has rows and the core tables are
queryable, printing row counts. Exits non-zero on any failure.

> **Never point `TARGET_DATABASE_URL` at production** — the restore drops and
> replaces objects. Use a scratch database (a temporary Railway DB, or a local
> `postgres:16` container as above).

This drill has been exercised against a throwaway Postgres: a seeded source
(3 migrations + tenants/users/gateways) dumped and restored cleanly into a
separate target with all rows intact.

## Disaster recovery (restore into production)

1. **Stop writes**: pause the API service (or put it in maintenance) so no new
   writes race the restore.
2. Provision a fresh Postgres (or use Railway's restore-to-new-service) and get
   its `DATABASE_URL`.
3. Restore the chosen backup into it (the drill's `pg_dump`/`pg_restore` steps).
4. Point the API's `DATABASE_URL` at the restored database.
5. On startup the API runs `Database.Migrate()` (`SchemaBootstrap.Apply`), which
   is a no-op if the backup already carried the latest migration.
6. Verify: `GET /health/ready` returns `ready` (it checks DB connectivity), then
   run the smoke checks (sign-in, a tenant read, a gateway heartbeat).
7. Resume writes.

## Related

- Migrations are managed by EF Core (`Database.Migrate()` on startup); the
  gated-rollout caveat for regulated production is ADR 0013.
- Readiness (`/health/ready`) is DB-aware — it returns 503 when the database is
  unreachable, so a restore-in-progress replica is not routed traffic.
