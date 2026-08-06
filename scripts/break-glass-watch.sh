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
#   1  break-glass use in the window    -> still in use; do NOT withdraw
#
# Usage:
#   scripts/break-glass-watch.sh [environment] [days]
#   scripts/break-glass-watch.sh production 30
#
# Requires the platform API to be reachable and an operator session or the admin token
# itself (GET /api/platform/security-events needs platform.security_event.read).
set -euo pipefail

ENVIRONMENT="${1:-staging}"
WINDOW_DAYS="${2:-7}"
SERVICE="${SERVICE:-Labs-MidWare}"

command -v railway >/dev/null 2>&1 || { echo "railway CLI not found (run: railway login)"; exit 2; }

case "$ENVIRONMENT" in
  production) BASE="${BASE:-https://lc.spottiq.com}" ;;
  staging)    BASE="${BASE:-https://labs-midware-staging.up.railway.app}" ;;
  *)          : "${BASE:?set BASE for a non-standard environment}" ;;
esac

echo "→ break-glass readiness: $ENVIRONMENT ($BASE), window = last ${WINDOW_DAYS}d"

TOKEN="${ADMIN_TOKEN:-$(railway variables --service "$SERVICE" --environment "$ENVIRONMENT" --json 2>/dev/null \
  | python3 -c 'import sys,json;print(json.load(sys.stdin).get("ControlPlane__AdminToken",""))')}"
[ -n "$TOKEN" ] || { echo "could not obtain a token for $ENVIRONMENT"; exit 2; }

events=$(curl -sS -m 30 -H "Authorization: Bearer $TOKEN" \
  "$BASE/api/platform/security-events?limit=500" 2>/dev/null || echo '[]')

printf '%s' "$events" | WINDOW_DAYS="$WINDOW_DAYS" python3 -c '
import json, os, sys
from datetime import datetime, timedelta, timezone

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
    sys.exit(1)

if in_window:
    print()
    print("  break-glass calls in window:")
    for e in in_window[:20]:
        print(f"    {e.get(\"at\",\"\")[:19]}  {e.get(\"detail\",\"\")}")
    print()
    print("✗ The admin token is still being used. Do NOT withdraw it — move the caller")
    print("  above onto a named platform role first.")
    sys.exit(1)

print()
print(f"✓ No break-glass use in the last {days}d. The token is a candidate for withdrawal.")
print("  Confirm operator workflows were actually exercised in this period — a quiet")
print("  window on an idle system is not evidence.")
'
