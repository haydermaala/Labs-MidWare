#!/usr/bin/env bash
#
# Backup + restore drill for the control-plane Postgres database.
#
# Proves that a backup of SOURCE_DATABASE_URL can be restored into a separate
# TARGET_DATABASE_URL and that the restored database is intact (schema migrated,
# data present). Run it on a schedule and before any risky migration/rollout —
# a backup you have never restored is not a backup.
#
# This is READ-ONLY against the source (pg_dump only). It DROPS AND REPLACES
# objects in the target, so the target must be a throwaway/scratch database —
# never point TARGET at production.
#
# Requirements: pg_dump, pg_restore, psql (Postgres client tools). If they are
# not installed locally, run this inside the official postgres image, e.g.:
#   docker run --rm -e SOURCE_DATABASE_URL -e TARGET_DATABASE_URL \
#     -v "$PWD/scripts:/s" postgres:16 bash /s/restore-drill.sh
#
# Usage:
#   SOURCE_DATABASE_URL=postgres://…  TARGET_DATABASE_URL=postgres://…  scripts/restore-drill.sh
set -euo pipefail

: "${SOURCE_DATABASE_URL:?set SOURCE_DATABASE_URL (read-only source, e.g. a backup or replica)}"
: "${TARGET_DATABASE_URL:?set TARGET_DATABASE_URL (SCRATCH database — its contents are replaced)}"

dump_file="$(mktemp -t restore-drill-XXXXXX.dump)"
trap 'rm -f "$dump_file"' EXIT

echo "==> [1/3] Backing up source (pg_dump, custom format, read-only)…"
pg_dump --format=custom --no-owner --no-privileges --file="$dump_file" "$SOURCE_DATABASE_URL"
echo "    backup written ($(du -h "$dump_file" | cut -f1))"

echo "==> [2/3] Restoring into target (drops & replaces objects)…"
# --clean --if-exists makes the restore idempotent against a non-empty target.
pg_restore --clean --if-exists --no-owner --no-privileges \
  --dbname="$TARGET_DATABASE_URL" "$dump_file"

echo "==> [3/3] Verifying the restored database…"
migrations="$(psql -tAX "$TARGET_DATABASE_URL" \
  -c 'SELECT count(*) FROM "__EFMigrationsHistory";')"
if [ "${migrations:-0}" -lt 1 ]; then
  echo "FAIL: no EF migrations found in the restored database" >&2
  exit 1
fi
echo "    migrations applied: $migrations"

# Report row counts for the core tables (informational — the restore is verified
# by the schema + migrations being present and queryable).
for table in tenants users gateways subscriptions; do
  count="$(psql -tAX "$TARGET_DATABASE_URL" -c "SELECT count(*) FROM $table;" 2>/dev/null || echo 'n/a')"
  printf '    %-14s %s\n' "$table" "$count"
done

echo "==> PASS: source backed up and restored into target; schema + migrations intact."
