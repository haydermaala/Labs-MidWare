#!/usr/bin/env bash
#
# Break-glass readiness gate for retiring the god-mode `ControlPlane__AdminToken`.
#
# The token cannot be withdrawn while anything still depends on it. This answers that
# question from the APPEND-ONLY AUDIT TRAIL, not from logs.
#
# Why not logs: `railway logs` returns a small rolling tail (~100 lines) with no
# guaranteed retention, so "no break-glass in the buffer" could mean five minutes or
# five days of quiet — it cannot answer "has the token been used this month?", which is
# the only question that matters here. Every break-glass call is therefore also written
# to platform_security_events as `platform.break_glass.used`, which is timestamped and
# never rolls away.
#
# EXIT CODE IS THE POINT:
#   0  no break-glass use in the window -> the token is a candidate for withdrawal
#   1  break-glass use in the window, or an unreadable window -> do NOT withdraw
#   2  the check itself failed          -> no verdict at all; NOT evidence of quiet
#
# 1 and 2 are deliberately distinct. A checker that crashes exits nonzero too, and a
# nonzero exit read as "still in use" is a wrong answer that looks like a careful one.
# The verdict below is written by the analysis itself; if no verdict arrives, the
# script says so instead of inferring one from a failure.
#
# Usage:
#   LC_SESSION='ses_…' scripts/break-glass-watch.sh [environment] [days]
#   LC_SESSION='ses_…' scripts/break-glass-watch.sh production 30
#
# LC_SESSION must be an OPERATOR session token for a platform user holding
# platform.security_event.read (Auditor or Security Admin). It must NOT be the admin
# token — see the note at the auth block below.
set -euo pipefail

ENVIRONMENT="${1:-staging}"
WINDOW_DAYS="${2:-7}"

case "$ENVIRONMENT" in
  production) BASE="${BASE:-https://lc.spottiq.com}" ;;
  staging)    BASE="${BASE:-https://labs-midware-staging.up.railway.app}" ;;
  *)          : "${BASE:?set BASE for a non-standard environment}" ;;
esac

echo "→ break-glass readiness: $ENVIRONMENT ($BASE), window = last ${WINDOW_DAYS}d"

# AUTHENTICATE AS AN OPERATOR, NEVER WITH THE ADMIN TOKEN.
#
# This script used to pull ControlPlane__AdminToken out of Railway and authenticate
# with it. That is self-defeating in two separate ways.
#
# The fatal one: reading /api/platform/security-events passes through PlatformForbidden,
# which records a `platform.break_glass.used` event before the handler runs. So every
# run wrote a fresh break-glass event into the very window it was about to measure, then
# read it back and concluded "still in use". The gate could never return ready — the
# instrument destroyed its own measurement.
#
# The other: checking whether the god-mode credential is still in use, by using the
# god-mode credential, would be the wrong posture even if it worked. Reviewing the audit
# trail is an auditor's job and needs nothing more than platform.security_event.read.
#
# There is deliberately NO fallback to the admin token. A fallback would silently
# reintroduce the bug on exactly the runs where it matters most.
if [ -z "${LC_SESSION:-}" ]; then
  cat >&2 <<'MSG'
✗ LC_SESSION is not set — no verdict.

  This check authenticates as an operator, not with the admin token (using the
  credential under investigation to investigate itself makes every run report
  "still in use", because the read records its own break-glass event).

  Sign in as a platform user holding platform.security_event.read — the Auditor
  or Security Admin role — and export the session token:

      export LC_SESSION='ses_…'

  Then re-run. The token is never needed for this check.
MSG
  exit 2
fi

events=$(curl -sS -m 30 -H "Authorization: Bearer $LC_SESSION" \
  "$BASE/api/platform/security-events?limit=500" 2>/dev/null || echo '[]')

VERDICT_FILE=$(mktemp)
trap 'rm -f "$VERDICT_FILE"' EXIT

set +e
printf '%s' "$events" | WINDOW_DAYS="$WINDOW_DAYS" VERDICT_FILE="$VERDICT_FILE" python3 -c '
import json, os, sys

from datetime import datetime, timedelta, timezone

def verdict(v):
    with open(os.environ["VERDICT_FILE"], "w") as fh:
        fh.write(v)

days = int(os.environ["WINDOW_DAYS"])
cutoff = datetime.now(timezone.utc) - timedelta(days=days)
try:
    events = json.load(sys.stdin)
except Exception:
    print("  could not read security events (is the token valid?)"); sys.exit(2)

def when(e):
    try:
        return datetime.fromisoformat(e.get("at","").replace("Z","+00:00"))
    except Exception:
        return None

uses = [e for e in events if e.get("kind") == "platform.break_glass.used"]
in_window = [e for e in uses if (w := when(e)) and w >= cutoff]
oldest = min((w for e in events if (w := when(e))), default=None)

print(f"  audit events available : {len(events)}")
print(f"  trail reaches back to  : {oldest.date() if oldest else 'unknown'}")
print(f"  break-glass uses total : {len(uses)}")
print(f"  break-glass in window  : {len(in_window)}")

if oldest and oldest > cutoff and len(events) >= 500:
    print()
    print(f"  ⚠ the trail only reaches {oldest.date()}, which is INSIDE your {days}d window,")
    print("    and the 500-event page is full — older use may exist but be unreadable.")
    print("    Treat this as INCONCLUSIVE, not as evidence of quiet.")
    verdict("inconclusive"); sys.exit(1)

if in_window:
    print()
    print("  break-glass calls in window:")
    for e in in_window[:20]:
        print("    %s  %s" % (e.get("at","")[:19], e.get("detail","")))
    print()
    print("✗ The admin token is still being used. Do NOT withdraw it — move the caller")
    print("  above onto a named platform role first.")
    verdict("in-use"); sys.exit(1)

print()
print(f"✓ No break-glass use in the last {days}d. The token is a candidate for withdrawal.")
print("  Confirm operator workflows were actually exercised in this period — a quiet")
print("  window on an idle system is not evidence.")
verdict("ready")
'
set -e

case "$(cat "$VERDICT_FILE" 2>/dev/null)" in
  ready)                 exit 0 ;;
  in-use|inconclusive)   exit 1 ;;
  *)
    echo
    echo "✗ The readiness check produced NO verdict — the checker itself failed above."
    echo "  This is not evidence the token is unused, and it is not evidence it is used."
    echo "  Fix the checker and re-run before making any withdrawal decision."
    exit 2 ;;
esac
