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
#   scripts/break-glass-watch.sh [environment] [days]
#   scripts/break-glass-watch.sh production 30                  # token mode (degraded)
#   LC_SESSION='ses_…' scripts/break-glass-watch.sh production 30   # operator mode
#
# Two observer modes, and the difference is stated in the output every run:
#
#   OPERATOR (LC_SESSION set) — a session for a platform user holding
#     platform.security_event.read (Auditor or Security Admin). Sound: the observer is a
#     different principal from the subject, so nothing is excluded and nothing is blind.
#
#   TOKEN (fallback) — authenticates with the admin token. Reading the trail is itself a
#     break-glass use, so this mode must discount `platform:platform.security_event.read`
#     to avoid counting its own runs forever. That makes it BLIND to genuine token reads
#     of the audit log. It still sees every mutation and every other permission, so it is
#     useful — it is just weaker evidence, and it says so.
#
# Get a session token: sign in to the console as an Auditor/Security Admin, then in the
# browser devtools console run  document.cookie.match(/lc_session=([^;]+)/)[1]
set -euo pipefail

ENVIRONMENT="${1:-staging}"
WINDOW_DAYS="${2:-7}"

case "$ENVIRONMENT" in
  production) BASE="${BASE:-https://lc.spottiq.com}" ;;
  staging)    BASE="${BASE:-https://labs-midware-staging.up.railway.app}" ;;
  *)          : "${BASE:?set BASE for a non-standard environment}" ;;
esac

echo "→ break-glass readiness: $ENVIRONMENT ($BASE), window = last ${WINDOW_DAYS}d"

# CHOOSE AN OBSERVER.
#
# Reading /api/platform/security-events passes through PlatformForbidden, which records a
# `platform.break_glass.used` event before the handler runs. So when the observer IS the
# admin token, every run writes a fresh event into the window it is about to measure and
# then reads it back — the instrument destroys its own measurement, and the verdict is
# "still in use" forever.
#
# An operator session avoids that entirely: different principal, nothing to discount.
# When one is not available we fall back to the token and discount the one permission the
# observer itself uses, which is honest but blind in that one spot. The mode is printed
# on every run so a result is never read as stronger than it is.
DISCOUNT_SELF_READS=0
if [ -n "${LC_SESSION:-}" ]; then
  OBSERVER="operator session"
  AUTH="$LC_SESSION"
else
  OBSERVER="ADMIN TOKEN (degraded)"
  DISCOUNT_SELF_READS=1
  # Every failure here must land on "no verdict" (exit 2), never on a bare nonzero that
  # set -e would surface as 1 — 1 means "the token is in use", and a lookup failure must
  # not be able to impersonate that answer. Hence the guards and the `|| true`.
  AUTH="${ADMIN_TOKEN:-}"
  if [ -z "$AUTH" ] && command -v railway >/dev/null 2>&1; then
    AUTH="$(railway variables --service "${SERVICE:-Labs-MidWare}" \
      --environment "$ENVIRONMENT" --json 2>/dev/null \
      | python3 -c 'import sys,json
try: print(json.load(sys.stdin).get("ControlPlane__AdminToken",""))
except Exception: print("")' 2>/dev/null || true)"
  fi
  if [ -z "$AUTH" ]; then
    cat >&2 <<'MSG'
✗ No observer credential — no verdict.

  Preferred: sign in to the console as a platform user holding
  platform.security_event.read (Auditor or Security Admin), then in the browser
  devtools console run

      document.cookie.match(/lc_session=([^;]+)/)[1]

  and export it:

      export LC_SESSION='ses_…'

  Fallback: make the admin token reachable (railway login, or ADMIN_TOKEN=…).
  That mode is blind to token reads of the audit log — see the header.
MSG
    exit 2
  fi
fi

echo "  observer: $OBSERVER"

events=$(curl -sS -m 30 -H "Authorization: Bearer $AUTH" \
  "$BASE/api/platform/security-events?limit=500" 2>/dev/null || echo '[]')

VERDICT_FILE=$(mktemp)
trap 'rm -f "$VERDICT_FILE"' EXIT

set +e
printf '%s' "$events" | WINDOW_DAYS="$WINDOW_DAYS" VERDICT_FILE="$VERDICT_FILE" \
  DISCOUNT_SELF_READS="$DISCOUNT_SELF_READS" python3 -c '
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

SELF_READ = "platform:platform.security_event.read"
discount = os.environ.get("DISCOUNT_SELF_READS") == "1"

all_uses = [e for e in events if e.get("kind") == "platform.break_glass.used"]
# In token mode the observer is the subject: this very request wrote a SELF_READ event,
# and so did every previous run. Counting them would pin the verdict at "in use" forever
# and say nothing about whether the token is doing real work. Discounting them is the
# price of that mode, and it is the reason an operator session is preferred.
uses = [e for e in all_uses if not (discount and e.get("detail") == SELF_READ)]
discounted = len(all_uses) - len(uses)
in_window = [e for e in uses if (w := when(e)) and w >= cutoff]
oldest = min((w for e in events if (w := when(e))), default=None)

print(f"  audit events available : {len(events)}")
print(f"  trail reaches back to  : {oldest.date() if oldest else 'unknown'}")
print(f"  break-glass uses total : {len(uses)}")
print(f"  break-glass in window  : {len(in_window)}")
if discount:
    print(f"  discounted (own reads) : {discounted}")

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
if discount:
    print()
    print("  ⚠ Observed with the ADMIN TOKEN, so token reads of the audit log itself were")
    print("    discounted and are invisible here. Every mutation and every other")
    print("    permission WAS counted. Before acting on this, re-run once with")
    print("    LC_SESSION set — that mode discounts nothing.")
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
