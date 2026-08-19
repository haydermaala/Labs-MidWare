#!/usr/bin/env bash
#
# Operator rehearsal — prove every operator workflow works WITHOUT the god-mode token.
#
# WHY THIS EXISTS
#
# scripts/break-glass-watch.sh answers "was the token used in this window?". On an idle
# system that question has no denominator: production ran 13 days with zero break-glass
# use and zero operator activity, which shows only that nobody was working. The gate
# correctly reports INCONCLUSIVE, and it will keep doing so until the operator workflows
# are actually exercised.
#
# This exercises them, deliberately, through named platform roles only. It does two jobs
# at once:
#
#   1. CAPABILITY — if a workflow 403s or 404s here, it still depends on the token, and
#      retirement would break it. That is the thing the gate cannot tell you.
#   2. EVIDENCE — it fills the window with real operator activity, so a subsequent
#      break-glass-watch run has something to be quiet ABOUT.
#
# The admin token is never sent. If ADMIN_TOKEN is set in the environment this refuses to
# run, because a rehearsal that silently falls back to god-mode proves the opposite of
# what it claims.
#
# IT MUTATES THE ENVIRONMENT. It creates a clearly-named tenant and a rehearsal user, and
# leaves the tenant SUSPENDED (not offboarded — offboarding is irreversible). Everything
# it creates is named with the run stamp so it can be identified and cleaned up.
#
# Usage:
#   export LC_SESSION=<paste the lc_session cookie value>   # then:
#   scripts/operator-rehearsal.sh staging          # rehearse somewhere safe first
#   scripts/operator-rehearsal.sh production
#   scripts/operator-rehearsal.sh production --dry-run
#
# The export is shown separately on purpose: a usage line containing a placeholder gets
# copied whole, and `LC_SESSION='ses_…'` then arrives as a literal credential.
#
# LC_SESSION must be a ROOT OWNER session (the workflows below span provisioning, role
# management and membership seeding; only Root Owner holds all of them). Root Owner also
# requires MFA and re-authentication every 10 minutes — if steps start failing with
# stepUp, sign in again and re-run.
#
# GETTING THE SESSION TOKEN. The lc_session cookie is HttpOnly, so document.cookie cannot
# read it — the browser deliberately hides it from JavaScript. Use the DevTools cookie
# inspector, which can show HttpOnly values:
#
#   Chrome/Edge : DevTools > Application > Storage > Cookies > <site> > lc_session > Value
#   Firefox     : DevTools > Storage > Cookies > <site> > lc_session
#   Safari      : Develop > Show Web Inspector > Storage > Cookies > lc_session
#
# The value starts with `ses_`. Copy it whole.
set -uo pipefail

ENVIRONMENT="${1:-staging}"
DRY_RUN=0
[ "${2:-}" = "--dry-run" ] && DRY_RUN=1

case "$ENVIRONMENT" in
  production) BASE="${BASE:-https://lc.spottiq.com}" ;;
  staging)    BASE="${BASE:-https://labs-midware-staging.up.railway.app}" ;;
  *)          : "${BASE:?set BASE for a non-standard environment}" ;;
esac

if [ -n "${ADMIN_TOKEN:-}" ]; then
  echo "✗ ADMIN_TOKEN is set. Unset it — the entire point is to prove these workflows" >&2
  echo "  succeed WITHOUT the token, and a run that could fall back proves nothing." >&2
  exit 2
fi
if [ -z "${LC_SESSION:-}" ]; then
  echo "✗ LC_SESSION is not set. Sign in to the console as Root Owner (MFA), then copy" >&2
  echo "  the session cookie from DevTools > Application > Cookies > lc_session > Value" >&2
  echo "  (it is HttpOnly, so document.cookie cannot read it), and export it:" >&2
  echo "      export LC_SESSION=ses_xxxxxxxx      # your real value" >&2
  exit 2
fi

# Reject the placeholder before touching the network. It is copied from documentation
# more often than anyone would like, and the resulting uniform 401s look exactly like a
# real finding.
case "$LC_SESSION" in
  "ses_…"|"ses_..."|"ses_"|"<session>"|"YOUR_SESSION")
    echo "✗ LC_SESSION is still the placeholder from the docs, not a real token." >&2
    echo "  Get the real one: DevTools > Application > Cookies > lc_session > Value." >&2
    echo "  (document.cookie cannot read it — the cookie is HttpOnly.)" >&2
    exit 2 ;;
esac
if [ "${LC_SESSION#ses_}" = "$LC_SESSION" ]; then
  echo "✗ LC_SESSION does not start with 'ses_', so it is not a session token." >&2
  echo "  If you pasted the admin token: that is the credential this rehearsal exists to" >&2
  echo "  prove unnecessary, and using it here would prove the opposite." >&2
  exit 2
fi

STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
PASS=0; FAIL=0; SKIP=0
declare -a RESULTS=()

# NB: this must NOT be called inside $( ), or HTTP_STATUS is set in a subshell and never
# reaches the caller — which under `set -u` shows up as an unbound-variable crash on the
# first real request. The body goes to $BODY_FILE; the status stays in this shell.
HTTP_STATUS=000
BODY_FILE="$(mktemp)"
trap 'rm -f "$BODY_FILE"' EXIT

api() { # method path [json]  -> body in $BODY_FILE, status in $HTTP_STATUS
  local method="$1" path="$2" data="${3:-}"
  if [ -n "$data" ]; then
    HTTP_STATUS="$(curl -sS -m 30 -o "$BODY_FILE" -w '%{http_code}' -X "$method" \
      -H "Authorization: Bearer $LC_SESSION" -H "Content-Type: application/json" \
      -d "$data" "$BASE$path" 2>/dev/null || echo 000)"
  else
    HTTP_STATUS="$(curl -sS -m 30 -o "$BODY_FILE" -w '%{http_code}' -X "$method" \
      -H "Authorization: Bearer $LC_SESSION" "$BASE$path" 2>/dev/null || echo 000)"
  fi
}

jget() { python3 -c 'import sys,json
try: print(json.load(sys.stdin).get(sys.argv[1],""))
except Exception: print("")' "$1" 2>/dev/null; }

step() { # name expected-status method path [json]
  local name="$1" want="$2" method="$3" path="$4" data="${5:-}"
  if [ "$DRY_RUN" = "1" ]; then
    RESULTS+=("  DRY   $name  ($method $path)"); SKIP=$((SKIP+1)); LAST_BODY=""; return 0
  fi
  api "$method" "$path" "$data"
  LAST_BODY="$(cat "$BODY_FILE")"
  if [ "$HTTP_STATUS" = "$want" ]; then
    RESULTS+=("  PASS  $name  ($HTTP_STATUS)"); PASS=$((PASS+1)); return 0
  fi
  local hint=""
  case "$HTTP_STATUS" in
    401) hint=" — session invalid or lacks the platform role" ;;
    403) hint=" — DENIED. This workflow may still need the token, or needs step-up (re-auth)" ;;
    000) hint=" — could not reach $BASE" ;;
  esac
  RESULTS+=("  FAIL  $name  (got $HTTP_STATUS, want $want)$hint")
  RESULTS+=("        $(printf '%s' "$LAST_BODY" | head -c 200)")
  FAIL=$((FAIL+1)); return 1
}

echo "→ operator rehearsal: $ENVIRONMENT ($BASE)"
echo "  observer: ROOT OWNER session (the admin token is never sent)"
[ "$DRY_RUN" = "1" ] && echo "  DRY RUN — nothing will be created"
echo "  run stamp: $STAMP"
echo

# Baseline the break-glass count so the rehearsal can verify its OWN premise at the end.
#
# Refusing to run when ADMIN_TOKEN is set is not enough: nothing stops someone pasting the
# token into LC_SESSION, and then every step below would pass as break-glass while
# appearing to prove the workflows need no token. The app records every token-authorized
# request, so the trail settles it — if this run wrote any break-glass event, the session
# WAS the token and the result is worthless.
bg_count() {
  api GET "/api/platform/security-events?limit=500"
  python3 -c 'import sys,json
try: print(len([e for e in json.load(sys.stdin) if e.get("kind") == "platform.break_glass.used"]))
except Exception: print(-1)' < "$BODY_FILE" 2>/dev/null || echo -1
}
BG_BEFORE=-1

# PREFLIGHT — establish that the session works BEFORE claiming anything about workflows.
#
# Without this, an unauthenticated session produced six identical 401s and the summary
# announced "6 workflow(s) failed ... Do NOT retire the token". Nothing had been tested.
# That is a confident wrong answer of exactly the kind this whole effort exists to
# prevent: a broken check must report NO VERDICT (exit 2), never a finding (exit 1).
#
# whoami distinguishes the three cases precisely. It returns 401 only when the session
# does not authenticate at all; a valid session with no platform roles still gets 200
# with an empty list.
if [ "$DRY_RUN" = "0" ]; then
  api GET "/api/platform/whoami"
  case "$HTTP_STATUS" in
    200) : ;;
    401)
      echo "✗ The session did not authenticate — NOTHING WAS TESTED." >&2
      echo >&2
      echo "  This is not a finding about the admin token. It means LC_SESSION is not a" >&2
      echo "  valid session: wrong value, expired, or logged out elsewhere." >&2
      echo >&2
      echo "  Get a fresh one: sign in to $BASE as Root Owner (MFA), then" >&2
      echo "  DevTools > Application > Cookies > lc_session > Value. It starts with ses_." >&2
      echo "  document.cookie cannot read it — the cookie is HttpOnly." >&2
      exit 2 ;;
    000)
      echo "✗ Could not reach $BASE — NOTHING WAS TESTED." >&2
      exit 2 ;;
    *)
      echo "✗ Unexpected $HTTP_STATUS from whoami — NOTHING WAS TESTED." >&2
      head -c 300 "$BODY_FILE" >&2; echo >&2
      exit 2 ;;
  esac

  ROLES="$(python3 -c 'import sys,json
try: print(",".join(json.load(sys.stdin).get("roles",[])))
except Exception: print("")' < "$BODY_FILE" 2>/dev/null)"
  if [ -z "$ROLES" ]; then
    echo "✗ The session authenticates but holds NO platform roles — nothing was tested." >&2
    echo "  Sign in as the Root Owner account, not an ordinary tenant user." >&2
    exit 2
  fi
  case "$ROLES" in
    *platform-root-owner*) : ;;
    *)
      echo "✗ The session holds [$ROLES] but not platform-root-owner — nothing was tested." >&2
      echo "  These workflows span provisioning, role management and membership seeding;" >&2
      echo "  only Root Owner holds all three. Sign in as Root Owner and re-run." >&2
      exit 2 ;;
  esac
  echo "  preflight: session authenticates, roles = $ROLES"

  BG_BEFORE="$(bg_count)"
fi

# (whoami is covered by the preflight above.)

# 1. Read surfaces — the day-to-day operator views.
step "list tenants"          200 GET "/api/platform/tenants"
step "platform overview"     200 GET "/api/platform/overview"
step "read security events"  200 GET "/api/platform/security-events?limit=10"

# 2. Provision a tenant. This is the workflow the token's POST /api/tenants used to own.
REH_TENANT_NAME="retirement-rehearsal-$STAMP"
step "provision a tenant" 201 POST "/api/platform/tenants" \
  "$(printf '{"name":"%s"}' "$REH_TENANT_NAME")"
TENANT_ID="$(printf '%s' "${LAST_BODY:-}" | jget id)"
[ -n "$TENANT_ID" ] && echo "  tenant: $TENANT_ID"

# 3. Create an operator account — replaces POST /api/admin/users.
REH_EMAIL="rehearsal-$STAMP@example.invalid"
REH_PASS="$(head -c 32 /dev/urandom | base64 | tr -d '/+=' | head -c 24)aA1!"
step "create a user" 201 POST "/api/platform/users" \
  "$(python3 -c 'import json,sys; print(json.dumps({"email":sys.argv[1],"password":sys.argv[2]}))' "$REH_EMAIL" "$REH_PASS")"
REH_USER_ID="$(printf '%s' "${LAST_BODY:-}" | jget id)"

# 4. Seat that user as the tenant's first owner — replaces POST /api/admin/memberships.
if [ -n "${TENANT_ID:-}" ] && [ -n "${REH_USER_ID:-}" ]; then
  step "seat the first owner" 204 POST "/api/platform/tenants/$TENANT_ID/memberships" \
    "$(printf '{"userId":"%s","role":"owner"}' "$REH_USER_ID")"
  # And prove the guard its legacy twin lacks: a second seeding must be refused.
  step "refuse a second owner (409)" 409 POST "/api/platform/tenants/$TENANT_ID/memberships" \
    "$(printf '{"userId":"%s","role":"owner"}' "$REH_USER_ID")"
else
  RESULTS+=("  SKIP  seat the first owner — no tenant or user id"); SKIP=$((SKIP+2))
fi

# 5. Platform role lifecycle: grant then revoke.
if [ -n "${REH_USER_ID:-}" ]; then
  step "grant a platform role" 201 POST "/api/platform/role-assignments" \
    "$(printf '{"userId":"%s","role":"platform-auditor","reason":"retirement rehearsal"}' "$REH_USER_ID")"
  ASSIGNMENT_ID="$(printf '%s' "${LAST_BODY:-}" | jget id)"
  if [ -n "${ASSIGNMENT_ID:-}" ]; then
    step "revoke that platform role" 204 DELETE "/api/platform/role-assignments/$ASSIGNMENT_ID"
  fi
  # 5b. Separation of duty must actually refuse the conflicting pair.
  step "grant support-engineer" 201 POST "/api/platform/role-assignments" \
    "$(printf '{"userId":"%s","role":"platform-support-engineer","reason":"rehearsal"}' "$REH_USER_ID")"
  SE_ID="$(printf '%s' "${LAST_BODY:-}" | jget id)"
  step "SoD refuses security-admin (409)" 409 POST "/api/platform/role-assignments" \
    "$(printf '{"userId":"%s","role":"platform-security-admin","reason":"rehearsal"}' "$REH_USER_ID")"
  [ -n "${SE_ID:-}" ] && step "revoke support-engineer" 204 DELETE "/api/platform/role-assignments/$SE_ID"
fi

# 6. Tenant lifecycle: suspend, then reactivate.
if [ -n "${TENANT_ID:-}" ]; then
  step "suspend the tenant"    204 POST "/api/platform/tenants/$TENANT_ID/suspend"
  step "reactivate the tenant" 204 POST "/api/platform/tenants/$TENANT_ID/reactivate"
  step "suspend again (leave parked)" 204 POST "/api/platform/tenants/$TENANT_ID/suspend"
fi

echo
printf '%s\n' "${RESULTS[@]}"
echo
echo "  passed $PASS, failed $FAIL, skipped $SKIP"

if [ "$DRY_RUN" = "1" ]; then
  echo
  echo "Dry run — nothing was created."
  exit 0
fi

echo
if [ -n "${TENANT_ID:-}" ]; then
  echo "  Left behind (suspended, safe to keep or offboard later):"
  echo "    tenant $TENANT_ID  \"$REH_TENANT_NAME\""
  echo "    user   ${REH_USER_ID:-?}  $REH_EMAIL"
  echo "  The password was generated and not stored anywhere; the account cannot be"
  echo "  signed into and exists only as the membership target."
fi

# Verify the premise before reporting anything as proof.
if [ "$BG_BEFORE" -ge 0 ]; then
  BG_AFTER="$(bg_count)"
  if [ "$BG_AFTER" -gt "$BG_BEFORE" ]; then
    echo
    echo "✗ This run recorded $((BG_AFTER - BG_BEFORE)) break-glass event(s), so LC_SESSION IS the"
    echo "  admin token, not an operator session. Every step above was authorized by the"
    echo "  very credential this rehearsal exists to prove unnecessary. The result proves"
    echo "  nothing — sign in as a Root Owner and use that session token instead."
    exit 2
  fi
  echo "  premise verified: 0 break-glass events recorded during this run."
fi

echo
if [ "$FAIL" -gt 0 ]; then
  echo "✗ $FAIL workflow(s) failed after the session was verified working."
  echo
  echo "  A 403 is the meaningful case: that workflow cannot be done through a named"
  echo "  platform role, so it still depends on the token and retiring it would break"
  echo "  the workflow — unless it is a stepUp, which just means the 10-minute Root"
  echo "  Owner freshness window lapsed mid-run. Re-authenticate and re-run to tell"
  echo "  those apart. Other codes are ordinary bugs in the workflow itself."
  exit 1
fi

echo "✓ Every operator workflow completed through named platform roles, with the admin"
echo "  token never sent. Now re-run the readiness gate over a window containing this run:"
echo
echo "      LC_SESSION=\$LC_SESSION scripts/break-glass-watch.sh $ENVIRONMENT 30"
echo
echo "  It should now report operator activity > 0 and break-glass 0 — which is a real"
echo "  green light, unlike a quiet window on an idle system."
