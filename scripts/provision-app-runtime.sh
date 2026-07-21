#!/usr/bin/env bash
#
# Provision the least-privilege `app_runtime` Postgres role for Row-Level
# Security (ADR 0018). The application connects as this role at runtime; because
# it is NOSUPERUSER / NOBYPASSRLS and owns no tables, the RLS policies are
# enforced against it. Migrations keep running as the owner role via a separate
# connection (MIGRATION_DATABASE_URL) — this role never runs DDL.
#
# Idempotent: safe to re-run. Creates the role if missing, (re)sets its password,
# and (re)grants least-privilege DML on all current and future application tables.
# It performs NO DDL on application tables and touches NO data — only roles/grants.
#
# Run it against STAGING first, verify, then production, as part of the staged
# rollout in docs/operations/rls-rollout.md.
#
# Requirements: psql. If not installed locally, run inside the postgres image:
#   docker run --rm -e ADMIN_DATABASE_URL -e APP_RUNTIME_PASSWORD -e OWNER_ROLE \
#     -v "$PWD/scripts:/s" postgres:16 bash /s/provision-app-runtime.sh
#
# Usage:
#   ADMIN_DATABASE_URL=postgres://owner:…@host:5432/db \
#   APP_RUNTIME_PASSWORD='<strong-secret>' \
#   [OWNER_ROLE=<migration/owner role>] \
#   scripts/provision-app-runtime.sh
#
# ADMIN_DATABASE_URL   an owner/superuser connection that may CREATEROLE + GRANT.
# APP_RUNTIME_PASSWORD the password for app_runtime. Never committed; supply at
#                      run time. (If the server logs DDL, rotate it afterwards, or
#                      pre-hash — see the note at the end.)
# OWNER_ROLE           the role that runs migrations and therefore owns the tables;
#                      used for ALTER DEFAULT PRIVILEGES so future tables are
#                      auto-granted. Defaults to the admin connection's role.
set -euo pipefail

: "${ADMIN_DATABASE_URL:?set ADMIN_DATABASE_URL (an owner/superuser connection that can CREATEROLE + GRANT)}"
: "${APP_RUNTIME_PASSWORD:?set APP_RUNTIME_PASSWORD (supplied at run time, never committed)}"

RUNTIME_ROLE="app_runtime"

# The role that owns/creates the tables (migrations run as it). Default: whoever
# ADMIN_DATABASE_URL connects as — correct when the admin IS the migration owner.
owner="${OWNER_ROLE:-}"
if [ -z "$owner" ]; then
  owner="$(psql "$ADMIN_DATABASE_URL" -X -t -A -c 'SELECT current_user;')"
fi
echo "→ provisioning role '$RUNTIME_ROLE' (table owner for default privileges: '$owner')"

# 1. Create the role if missing (CREATE ROLE has no IF NOT EXISTS), checked in the
#    shell so no psql variable is needed inside a dollar-quoted DO block.
if [ "$(psql "$ADMIN_DATABASE_URL" -X -t -A \
          -c "SELECT 1 FROM pg_roles WHERE rolname = '$RUNTIME_ROLE'")" != "1" ]; then
  psql "$ADMIN_DATABASE_URL" -X -v ON_ERROR_STOP=1 -c "CREATE ROLE \"$RUNTIME_ROLE\" LOGIN;"
fi

# 2. Pin attributes + grants in one transaction; the password flows over stdin
#    (not argv), and psql :'…' / :"…" substitution is used only outside DO blocks.
PGOPTIONS='--client-min-messages=warning' \
psql "$ADMIN_DATABASE_URL" -X -v ON_ERROR_STOP=1 \
     -v runtime="$RUNTIME_ROLE" -v owner="$owner" -v pw="$APP_RUNTIME_PASSWORD" <<'SQL'
BEGIN;

-- Least privilege, explicit every run so a pre-existing role is corrected too.
ALTER ROLE :"runtime" LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOREPLICATION;
ALTER ROLE :"runtime" PASSWORD :'pw';

-- 2. Read/write DML on the schema + all current application tables & sequences.
GRANT USAGE ON SCHEMA public TO :"runtime";
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO :"runtime";
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO :"runtime";

-- 3. Future tables/sequences created by the owner (i.e. by later migrations) are
--    auto-granted, so a new migration never silently locks the runtime out.
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner" IN SCHEMA public
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO :"runtime";
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner" IN SCHEMA public
  GRANT USAGE, SELECT ON SEQUENCES TO :"runtime";

-- 4. Never let the runtime touch migration bookkeeping.
REVOKE ALL ON TABLE "__EFMigrationsHistory" FROM :"runtime";

COMMIT;
SQL

echo "→ verifying least-privilege invariants…"
psql "$ADMIN_DATABASE_URL" -X -v ON_ERROR_STOP=1 -v runtime="$RUNTIME_ROLE" <<'SQL'
\pset footer off
-- MUST be: can log in, NOT superuser, does NOT bypass RLS.
SELECT rolcanlogin AS can_login, rolsuper AS is_superuser, rolbypassrls AS bypasses_rls
FROM pg_roles WHERE rolname = :'runtime';
-- MUST be 0: the runtime owns no tables (owners are exempt from their own RLS).
SELECT count(*) AS tables_owned_must_be_zero
FROM pg_tables WHERE schemaname = 'public' AND tableowner = :'runtime';
SQL

cat <<EOF

✔ Done. '$RUNTIME_ROLE' is provisioned. Confirm above: can_login=t, is_superuser=f,
  bypasses_rls=f, tables_owned_must_be_zero=0.

Next (see docs/operations/rls-rollout.md):
  • Point the app's runtime DATABASE_URL at $RUNTIME_ROLE.
  • Keep MIGRATION_DATABASE_URL pointed at the owner role ('$owner').

Note: if this database logs DDL (log_statement = ddl|all), the ALTER ROLE …
PASSWORD statement may appear in server logs. Rotate the password afterwards, or
set it out-of-band with psql's \\password (which sends a SCRAM hash, not plaintext).
EOF
