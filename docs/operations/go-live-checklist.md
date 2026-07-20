# Go-live checklist — control plane

Run before promoting a build to production and at cutover. Each item points at
the tool or runbook that satisfies it. This complements the readiness checklist
in `PRODUCTION_EXECUTION_PLAN.md §11`.

## Pre-deploy (every release)

- [ ] `scripts/verify.sh` green locally (fmt/lint/build/test across Rust, .NET, TS).
- [ ] CI green on the merge commit (build, tests, `pnpm audit`, `cargo deny`, gitleaks).
- [ ] Migrations reviewed — additive/backwards-compatible; no destructive column drops
      without a two-step deploy (see ADR 0013).
- [ ] A recent backup exists **and** the restore drill passed
      (`scripts/restore-drill.sh` → PASS). See `backup-restore.md`.

## Deploy

- [ ] Deploy to staging; wait for the container to report healthy.
- [ ] `scripts/smoke.sh https://labs-midware-staging.up.railway.app` → SMOKE PASS.
- [ ] Promote to production; wait for healthy.
- [ ] `scripts/smoke.sh https://<production-url>` → SMOKE PASS.

## Security & secrets

- [ ] No secrets in the repo (gitleaks clean); every secret lives only in the
      environment's own Railway variables, scoped per environment (staging's
      admin/DB creds differ from production's — cross-env isolation verified).
- [ ] Security headers present in production (the smoke test asserts nosniff,
      DENY, HSTS, CSP).
- [ ] Readiness is DB-aware (`/health/ready` → 503 when the DB is unreachable),
      so a bad replica is never routed traffic.
- [ ] Rate limiting and CORS reflect production origins.

## Rollback

- [ ] The previous known-good build is identifiable (Git SHA / Railway deploy).
- [ ] Rollback = redeploy the previous image; because startup migration is a
      no-op when the schema already matches, a redeploy is safe. If a migration
      must be reverted, restore from backup (`backup-restore.md`) — never hand-edit
      production schema.
- [ ] `scripts/smoke.sh` green again after rollback.

## Human gates (cannot be automated)

- [ ] **Live Stripe**: test-mode keys + price ids + webhook endpoint set as
      `Stripe__*` Railway vars (`billing-stripe-enablement.md`). No customer-facing
      prices published without explicit approval (the pricing gate).
- [ ] **Signed installers**: Apple Developer ID + Windows Authenticode identities
      supplied (`desktop-packaging.md`).
- [ ] **DNS cutover** (Phase H): replace the two Hostinger `A` records for
      `lc.spottiq.com` with one `CNAME` → Railway; do not touch apex/mail/MX/SPF/
      DKIM/DMARC. Verify TLS, canonical URL, secure cookies, CORS, and email links.
- [ ] **No real analyzer result release** until separate clinical validation and
      sign-off (Phase I).
