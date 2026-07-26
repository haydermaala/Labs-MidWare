#!/usr/bin/env bash
#
# RLS staging smoke — a fast automated pass over the health and tenant-scoped read
# paths of a staging deploy running as `app_runtime` under FORCE RLS (see
# docs/operations/rls-staging-smoke.md). It catches the two RLS failure
# signatures cheaply: a path that 500s (missed scope on a write) and a list that
# comes back empty where data exists (missed scope on a read — RLS fails closed).
#
# The manual cross-tenant isolation check (§5 of the checklist) is the real proof
# of isolation and is NOT automated here.
#
# Reads use the admin token (which bypasses the membership check but still runs the
# store queries as app_runtime with the tenant GUC set — so it does exercise RLS).
#
# Usage:
#   STAGING_URL=https://staging.example ADMIN_TOKEN=<bearer> TENANT_ID=ten_… \
#   [OPERATOR_EMAIL=… OPERATOR_PASSWORD=…] scripts/staging-smoke.sh
set -euo pipefail

: "${STAGING_URL:?set STAGING_URL (staging base URL)}"
: "${ADMIN_TOKEN:?set ADMIN_TOKEN (admin bearer token)}"
: "${TENANT_ID:?set TENANT_ID (a tenant the checks read)}"

BASE="${STAGING_URL%/}"
pass=0
fail=0
warn=0

# status NAME METHOD PATH EXPECTED
status() {
  local name=$1 method=$2 path=$3 expect=$4 code
  code=$(curl -sS -m 20 -o /dev/null -w '%{http_code}' -X "$method" \
    -H "Authorization: Bearer $ADMIN_TOKEN" "$BASE$path" 2>/dev/null || echo 000)
  if [ "$code" = "$expect" ]; then
    printf '  ✓ %-40s %s\n' "$name" "$code"; pass=$((pass + 1))
  else
    printf '  ✗ %-40s got %s, want %s\n' "$name" "$code" "$expect"; fail=$((fail + 1))
  fi
}

# nonempty NAME PATH — 200 and a JSON array that is not "[]" (empty ⇒ fail-closed?)
nonempty() {
  local name=$1 path=$2 body code
  body=$(curl -sS -m 20 -w $'\n%{http_code}' \
    -H "Authorization: Bearer $ADMIN_TOKEN" "$BASE$path" 2>/dev/null || printf '\n000')
  code=${body##*$'\n'}
  body=${body%$'\n'*}
  if [ "$code" != "200" ]; then
    printf '  ✗ %-40s got %s, want 200\n' "$name" "$code"; fail=$((fail + 1)); return
  fi
  if printf '%s' "$body" | tr -d '[:space:]' | grep -qx '\[\]'; then
    printf '  ⚠ %-40s 200 but EMPTY — confirm no data exists\n' "$name"; warn=$((warn + 1))
  else
    printf '  ✓ %-40s 200, non-empty\n' "$name"; pass=$((pass + 1))
  fi
}

echo "→ RLS staging smoke against $BASE (tenant $TENANT_ID)"

echo "health:"
status "GET /health"        GET /health        200
status "GET /health/ready"  GET /health/ready  200

if [ -n "${OPERATOR_EMAIL:-}" ] && [ -n "${OPERATOR_PASSWORD:-}" ]; then
  echo "identity (login exercises the platform-audit path under RLS):"
  login=$(curl -sS -m 20 -o /dev/null -w '%{http_code}' -X POST \
    -H 'Content-Type: application/json' \
    -d "{\"email\":\"$OPERATOR_EMAIL\",\"password\":\"$OPERATOR_PASSWORD\"}" \
    "$BASE/api/auth/login" 2>/dev/null || echo 000)
  if [ "$login" = "200" ]; then printf '  ✓ %-40s 200\n' "POST /api/auth/login"; pass=$((pass + 1))
  else printf '  ✗ %-40s got %s, want 200\n' "POST /api/auth/login" "$login"; fail=$((fail + 1)); fi
fi

echo "tenant-scoped reads (empty ⇒ possible missed scope):"
status   "GET /settings"  GET "/api/tenants/$TENANT_ID/settings" 200
nonempty "GET /gateways"      "/api/tenants/$TENANT_ID/gateways"
nonempty "GET /audit"         "/api/tenants/$TENANT_ID/audit"
status   "GET /billing"   GET "/api/tenants/$TENANT_ID/billing"  200
nonempty "GET /members"       "/api/tenants/$TENANT_ID/members"

echo
echo "→ pass=$pass  fail=$fail  warn(empty)=$warn"
echo "  Still do the manual cross-tenant isolation check (rls-staging-smoke.md §5)"
echo "  and the log scan for 'row-level security' before promoting."
[ "$fail" -eq 0 ]
