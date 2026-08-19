#!/usr/bin/env bash
#
# Repair/finish an owner account when the user already exists (HTTP 409 on
# create). Logs in to find the user id (proving the password), then grants owner
# membership on the lab (idempotent — tolerates "already a member"). Secrets are
# read at the terminal, never echoed or logged.
#
# Usage:
#   scripts/grant-owner.sh
#   EMAIL=you@lab.com TENANT_ID=ten_abc BASE=https://lc.spottiq.com scripts/grant-owner.sh
set -euo pipefail

BASE="${BASE:-https://lc.spottiq.com}"
TEN="${TENANT_ID:-ten_6f3d47e251b24426ae14835fda536637}"
EMAIL="${EMAIL:-lc@spottiq.com}"
mkjson() { python3 -c 'import json,sys; print(json.dumps(dict(zip(sys.argv[1::2], sys.argv[2::2]))))' "$@"; }

echo "Finishing owner account for $EMAIL on $BASE"
printf 'Password for %s (not echoed): ' "$EMAIL"; read -rs PW </dev/tty; echo
printf 'Admin token (ControlPlane__AdminToken): '; read -rs ADMIN </dev/tty; echo

echo "==> Logging in to find your user id..."
RESP=$(curl -s -w $'\n%{http_code}' -X POST "$BASE/api/auth/login" \
  -H "Content-Type: application/json" -d "$(mkjson email "$EMAIL" password "$PW")")
CODE=$(printf '%s' "$RESP" | tail -n1)
BODY=$(printf '%s' "$RESP" | sed '$d')
if [ "$CODE" != "200" ]; then
  echo "Login failed (HTTP $CODE)." >&2
  echo "The existing account has a DIFFERENT password than what you typed." >&2
  echo "Ask Claude to trigger a password reset (SMTP works on production)." >&2
  exit 1
fi
USR=$(printf '%s' "$BODY" | python3 -c "import sys,json;print(json.load(sys.stdin)['user']['id'])")
TOK=$(printf '%s' "$BODY" | python3 -c "import sys,json;print(json.load(sys.stdin)['sessionToken'])")
echo "    login works. user: $USR"

echo "==> Granting owner membership on $TEN..."
GC=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$BASE/api/admin/memberships" \
  -H "Authorization: Bearer $ADMIN" -H "Content-Type: application/json" \
  -d "$(mkjson userId "$USR" tenantId "$TEN" role owner)")
case "$GC" in
  200|201|204) echo "    owner granted";;
  409)         echo "    already a member (fine)";;
  *)           echo "    membership grant returned HTTP $GC (may already be set)";;
esac

echo "==> Your memberships now:"
curl -s "$BASE/api/me/memberships" -H "Authorization: Bearer $TOK" \
  | python3 -c "import sys,json; ms=json.load(sys.stdin); [print('   -',m.get('tenantName'),'->',m.get('role')) for m in ms] or print('   (none)')" 2>/dev/null \
  || echo "   (could not list memberships — sign in to confirm)"

unset PW ADMIN TOK
echo
echo "DONE. Sign in at $BASE/sign-in as $EMAIL"
