#!/usr/bin/env bash
#
# Provision the first owner account for a LabConnect environment.
#
# Creates a laboratory (tenant), a user, and an owner membership, then verifies
# the login. Secrets are read at the terminal — the admin token and password are
# never passed on the command line, echoed, or logged.
#
# You need the environment's admin token (ControlPlane__AdminToken) — in Railway:
#   production -> Labs-MidWare service -> Variables -> ControlPlane__AdminToken
#
# Usage:
#   scripts/provision-owner.sh
#   BASE=https://labs-midware-staging.up.railway.app scripts/provision-owner.sh
#   TENANT_ID=ten_abc123 scripts/provision-owner.sh   # reuse an existing lab
set -euo pipefail

BASE="${BASE:-https://lc.spottiq.com}"

# Build a JSON object from key/value pairs. Single-quoted python so the shell
# never touches the braces (avoids brace expansion) and json.dumps escapes safely.
mkjson() { python3 -c 'import json,sys; print(json.dumps(dict(zip(sys.argv[1::2], sys.argv[2::2]))))' "$@"; }
getfield() { python3 -c "import sys,json;print(json.load(sys.stdin).get('$1',''))"; }

echo "Provisioning an owner account on: $BASE"
printf 'Admin token (ControlPlane__AdminToken): '; read -rs ADMIN </dev/tty; echo
printf 'Your email: '; read -r EMAIL </dev/tty
printf 'Choose a password (min 12 chars, not echoed): '; read -rs PW </dev/tty; echo
printf 'Confirm password: '; read -rs PW2 </dev/tty; echo
[ "$PW" = "$PW2" ] || { echo "Passwords do not match." >&2; exit 1; }
[ "${#PW}" -ge 12 ] || { echo "Password must be at least 12 characters." >&2; exit 1; }

auth=(-H "Authorization: Bearer $ADMIN" -H "Content-Type: application/json")

if [ -n "${TENANT_ID:-}" ]; then
  TEN="$TENANT_ID"
  echo "==> Using existing laboratory: $TEN"
else
  printf 'Laboratory name (e.g. Riverside Diagnostics): '; read -r LAB </dev/tty
  echo "==> Creating laboratory..."
  TEN=$(curl -fsS -X POST "$BASE/api/tenants" "${auth[@]}" -d "$(mkjson name "$LAB")" | getfield id)
  [ -n "$TEN" ] || { echo "Failed to create tenant (check the admin token)." >&2; exit 1; }
  echo "    tenant: $TEN"
fi

echo "==> Creating user..."
USR=$(curl -fsS -X POST "$BASE/api/admin/users" "${auth[@]}" -d "$(mkjson email "$EMAIL" password "$PW")" | getfield id)
[ -n "$USR" ] || { echo "Failed to create user (the email may already exist)." >&2; exit 1; }
echo "    user: $USR"

echo "==> Granting owner membership..."
curl -fsS -o /dev/null -X POST "$BASE/api/admin/memberships" "${auth[@]}" \
  -d "$(mkjson userId "$USR" tenantId "$TEN" role owner)"
echo "    granted: owner"

echo "==> Verifying login..."
CODE=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$BASE/api/auth/login" \
  -H "Content-Type: application/json" -d "$(mkjson email "$EMAIL" password "$PW")")
unset ADMIN PW PW2
if [ "$CODE" = "200" ]; then
  echo; echo "DONE. Sign in at $BASE/sign-in as: $EMAIL"
else
  echo "Login check returned HTTP $CODE - the account exists but sign-in did not succeed; re-check the password." >&2
  exit 1
fi
