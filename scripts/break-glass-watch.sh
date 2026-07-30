#!/usr/bin/env bash
#
# Break-glass readiness gate.
#
# The god-mode `ControlPlane__AdminToken` bypasses both authorization gates. It cannot
# be withdrawn while anything still depends on it — and the honest way to know that is
# to watch, not to guess.
#
# Every tenant-scoped call the token makes now logs at Warning:
#   "break-glass: the platform admin token acted in tenant … (permission …)"
# and every support-grant-backed access logs:
#   "support-access: user … acted in tenant … without membership …"
#
# This scans the service log buffer for both. EXIT CODE IS THE POINT:
#   0  no break-glass use observed  -> the token is a candidate for withdrawal
#   1  break-glass use observed     -> it is still doing routine work; do NOT withdraw
#
# Usage:
#   scripts/break-glass-watch.sh [environment]     # default: staging
#
# Run it repeatedly over a representative period (a business day of real traffic, or
# after exercising every operator workflow). A single quiet scan proves very little —
# see docs/operations/p1-p7-production-cutover.md on why elapsed time without traffic
# is not evidence.
set -euo pipefail

ENVIRONMENT="${1:-staging}"
SERVICE="${SERVICE:-Labs-MidWare}"

command -v railway >/dev/null 2>&1 || { echo "railway CLI not found (run: railway login)"; exit 2; }

echo "→ break-glass watch: service=$SERVICE environment=$ENVIRONMENT"

log=$(mktemp)
trap 'rm -f "$log"' EXIT

# `railway logs` streams; cap it so this terminates in CI/cron.
timeout "${LOG_TIMEOUT:-45}" railway logs --service "$SERVICE" --environment "$ENVIRONMENT" \
  >"$log" 2>/dev/null || true

lines=$(wc -l <"$log" | tr -d ' ')
breakglass=$(grep -c "break-glass:" "$log" || true)
support=$(grep -c "support-access:" "$log" || true)

echo "  log lines scanned      : $lines"
echo "  break-glass uses       : $breakglass"
echo "  support-access uses    : $support"

if [ "$breakglass" -gt 0 ]; then
  echo
  echo "  break-glass calls observed (tenant + permission):"
  grep "break-glass:" "$log" | sed 's/^/    /' | tail -20
  echo
  echo "✗ The admin token is still being used. Do NOT withdraw it yet — find the caller"
  echo "  above and move it onto a named platform role first."
  exit 1
fi

if [ "$support" -gt 0 ]; then
  echo
  echo "  support-access calls observed (expected, and healthy — this is the sanctioned"
  echo "  replacement for the token's cross-tenant reach):"
  grep "support-access:" "$log" | sed 's/^/    /' | tail -10
fi

echo
echo "✓ No break-glass use in this window. Repeat across a representative period before"
echo "  treating the token as withdrawable — one quiet scan is not evidence."
