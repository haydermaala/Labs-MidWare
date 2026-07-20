#!/usr/bin/env bash
#
# Deployed-environment smoke test for the control-plane API.
#
# Runs a handful of black-box checks against a live base URL: liveness,
# DB-aware readiness, security headers, the public (price-free) plan catalog,
# and that a tenant-scoped endpoint rejects anonymous access. Read-only and
# side-effect-free — safe to run against staging or production.
#
# Usage:
#   scripts/smoke.sh https://labs-midware-staging.up.railway.app
#   BASE_URL=https://lc.spottiq.com scripts/smoke.sh
set -euo pipefail

BASE_URL="${1:-${BASE_URL:-}}"
: "${BASE_URL:?usage: scripts/smoke.sh <base-url>}"
BASE_URL="${BASE_URL%/}"

pass=0
fail=0
check() { # check <description> <test-command...>
  local desc="$1"; shift
  if "$@" >/dev/null 2>&1; then
    printf '  \033[32mPASS\033[0m %s\n' "$desc"; pass=$((pass + 1))
  else
    printf '  \033[31mFAIL\033[0m %s\n' "$desc"; fail=$((fail + 1))
  fi
}

code() { curl -s -o /dev/null -w '%{http_code}' "$1"; }
body() { curl -s "$1"; }
header() { curl -s -D - -o /dev/null "$1" | tr -d '\r'; }
# Exported so the `bash -c` predicates below can call them.
export -f code body header

echo "Smoke test → $BASE_URL"

check "GET /health is 200"                 test "$(code "$BASE_URL/health")" = 200
check "GET /health/ready is 200 (DB reachable)" test "$(code "$BASE_URL/health/ready")" = 200
check "readiness reports 'ready'"          bash -c "body '$BASE_URL/health/ready' | grep -q '\"status\":\"ready\"'"

check "X-Content-Type-Options: nosniff"    bash -c "header '$BASE_URL/health' | grep -qi 'x-content-type-options: nosniff'"
check "X-Frame-Options: DENY"              bash -c "header '$BASE_URL/health' | grep -qi 'x-frame-options: DENY'"
check "Strict-Transport-Security present"  bash -c "header '$BASE_URL/health' | grep -qi 'strict-transport-security: max-age='"
check "Content-Security-Policy present"    bash -c "header '$BASE_URL/health' | grep -qi \"content-security-policy: default-src 'self'\""

check "GET /api/billing/plans is 200"      test "$(code "$BASE_URL/api/billing/plans")" = 200
# Pricing gate: the public catalog must never expose monetary figures.
check "plan catalog publishes no prices"   bash -c "! body '$BASE_URL/api/billing/plans' | grep -qiE 'price|amount|\\\$'"

check "tenant billing rejects anon (401)"  test "$(code "$BASE_URL/api/tenants/ten_smoke/billing")" = 401
check "telemetry ingest rejects anon (401)" bash -c "test \"\$(curl -s -o /dev/null -w '%{http_code}' -X POST '$BASE_URL/api/gateways/telemetry' -H 'Content-Type: application/json' -d '{}')\" = 401"

echo
if [ "$fail" -eq 0 ]; then
  echo "SMOKE PASS — $pass checks green"
else
  echo "SMOKE FAIL — $fail of $((pass + fail)) checks failed" >&2
  exit 1
fi
