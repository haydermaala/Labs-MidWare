# P1–P7 production cutover runbook

The enterprise multi-tenancy / RBAC / super-admin program (P1–P7) is staged as
three **stacked** pull requests, unmerged, on top of `main`. This runbook
orchestrates the whole rollout as one operation. It **references, not repeats**,
the RLS mechanics and rollback in [rls-rollout.md](./rls-rollout.md) — read that
first; this doc adds the multi-phase ordering and the P6/P7 specifics.

> **Status when written:** all code complete and tested (green CI); nothing merged
> to `main`. The remaining work is this cutover, which is deliberately human-gated:
> it touches the live database role, runs FORCE RLS against production, and
> bootstraps the first platform Root Owner.

## The stacked branches

Merge **in this order** — each builds on the one below:

| PR | Branch | Base | Scope |
|----|--------|------|-------|
| #76 | `integration/p1-p2-p3` | `main` | P1 RLS foundation + P2 permission engine + P3 scopes/roles/SoD |
| #77 | `feat/p6-platform-admin` | `integration/p1-p2-p3` | P6 platform super-admin (named platform roles, support access, offboarding, audit) + platform-admin frontend |
| #78 | `feat/p7-tenant-lifecycle` | `feat/p6-platform-admin` | P7 tenant lifecycle state machine + full §10.3 offboarding pipeline + §12 grace |

Because they are stacked, the cleanest merge is bottom-up: **#76 → #77 → #78**,
each into the next base, then the tip into `main`. Alternatively, fast-forward
`feat/p7-tenant-lifecycle` to `main` in one merge once #76 and #77 are approved
(the tip already contains all three). Do **not** cherry-pick — the RLS wiring and
migration ordering depend on the full chain.

## Migration inventory

Railway runs `Database.Migrate()` on boot, as the **owner** via
`MIGRATION_DATABASE_URL`. The full chain (27 migrations) applies in timestamp
order. The ones with production-safety weight:

- **`AddDeviceCredentialTenantId`** (…171514) — backfills `device_credentials.TenantId`
  from the owning gateway. Runs **before** RLS is enabled → no policy interaction.
- **`AddRowLevelSecurity`** (…171544, P1) — `ENABLE`+`FORCE` RLS + policies on the 10
  P1 tenant tables. **This is the cutover's risk centre.** See rls-rollout.md.
- **`AddP3RowLevelSecurity`** (…114529, P3) — `ENABLE`+`FORCE` RLS + policies on the 6
  P3 tables (scopes, role_assignments, sod_rules, custom_roles, role_permissions,
  approval_requests). Same pattern; validated on `postgres:16`.
- **`AddTenantStatus`** (…193725, P7) — adds the lifecycle `Status` column and
  **backfills it from the legacy booleans**. The `UPDATE` is bracketed with
  `ALTER TABLE tenants NO FORCE … / FORCE …` because `tenants` is already FORCE'd by
  this point and the migration/owner role is subject to policies on managed Postgres
  — without the bracket the bulk update would silently touch **0 rows**. This is in
  the migration itself; no operator action, but know it's there if you read the SQL.
- **`AddTenantOffboardingWindow`** (…200416, P7) — 2 nullable timestamps + a
  `LegalHold` bool, no backfill.

All platform tables (`platform_role_assignments`, `platform_support_access_grants`,
`platform_security_events`, `platform_offboard_requests`) and `permission_definitions`
are **global** (no `TenantId`, no RLS) — the `RlsCoverageTests` gate enforces that.

## Preconditions

Everything in [rls-rollout.md §Preconditions](./rls-rollout.md#preconditions), plus:

- [ ] `app_runtime` role provisioned on production (rls-rollout.md Step 4).
- [ ] `MIGRATION_DATABASE_URL` (owner) set on the production service.
- [ ] A restore-drill passed against the latest production backup.
- [ ] The authenticated staging soak below is green.

## Step A — Staging: authenticated soak of the full stack

Deploy the tip branch (`feat/p7-tenant-lifecycle`) to staging with the runtime on
`app_runtime` and `MIGRATION_DATABASE_URL` on the owner. Then exercise every surface
that RLS + the new engines gate — an unauthenticated smoke is not enough.

> ### ⚠️ An API-level cross-tenant check does NOT prove RLS
>
> Reading `/api/tenants/{A}/…` and `/api/tenants/{B}/…` and observing that each only
> returns its own rows proves the **application-layer filter** (`Where(TenantId == x)`),
> **not** RLS. That test passes identically with RLS disabled, so it cannot distinguish
> working isolation from none at all.
>
> **Prove RLS at the database layer, connected as `app_runtime`** (verify first that it
> reports `rolsuper = f` and `rolbypassrls = f`):
>
> ```sql
> -- must be 0: no tenant GUC bound ⇒ fail-closed
> SELECT count(*) FROM gateways;
> -- must be > 0: bound to a tenant that has gateways
> SELECT set_config('app.tenant_id','<tenant-A>',false);
> SELECT count(*) FROM gateways;
> -- must be 0: bound to A, B's rows are invisible
> SELECT count(*) FROM gateways WHERE "TenantId"='<tenant-B>';
> -- must be 0: a P3 table, proving the second RLS migration took effect
> SELECT set_config('app.tenant_id','',false);
> SELECT count(*) FROM scopes;
> ```
>
> Likewise, **a 2xx from a write path does not prove the write persisted** — an
> RLS-blocked `UPDATE` affects 0 rows and still returns success. After a heartbeat,
> confirm `gateways."LastSeenAt"` actually changed.

Use [rls-staging-smoke.md](./rls-staging-smoke.md) §1–§5 for the tenant-isolation checks,
and add these P2–P7 checks (the operator runs them; the harness cannot hold the admin
token):

- [ ] **P2 step-up:** a fresh-auth-gated action (change role, decommission) on a
      >10-min-stale session pops the step-up modal, re-auths, and retries.
- [ ] **P3 scopes/SoD:** a role granted at a child scope does **not** grant at the
      tenant root; a two-party approval cannot be self-approved.
- [ ] **P6 platform console:** sign in as a user with **no** platform role → `/platform`
      shows "no platform role". Grant a platform role (bootstrap token, below) → the
      matching sections appear; a tenant role grants **no** platform access.
- [ ] **P6 support access / offboarding:** request + distinct-party approve both work;
      self-approval is refused (SoD).
- [ ] **P7 lifecycle:** suspend/reactivate; begin offboarding → tenant shows
      `offboarding`; **archive is refused during cooling-off** and while a legal hold is
      set; cancel returns to active; export downloads the artifact.
- [ ] **P7 grace:** a `past_due` billing webhook moves the tenant to `grace`; recovery
      returns it to `active`.
- [ ] No `row-level security` errors in the logs across the soak.

> **MFA note:** offboarding (and other MFA-gated platform ops) need an
> **MFA-enrolled, MFA-satisfied** session — a no-MFA login has `MfaSatisfied=false`.
> Enrol MFA on the operator account used for these checks.

## Step B — Production cutover

In the maintenance window, following [rls-rollout.md Step 5](./rls-rollout.md#step-5--production-cutover):

1. [ ] Repoint production `DATABASE_URL` → `app_runtime`.
2. [ ] Merge the stack to `main` (**#76 → #77 → #78** order, or fast-forward the tip).
       Railway deploys; on boot the owner runs the full migration chain (both RLS
       migrations + the P7 columns) and runtime serves as `app_runtime`.
3. [ ] Immediately verify:
   - [ ] `/health/ready` green.
   - [ ] Sign in; fleet loads; a gateway heartbeat succeeds; billing/audit reads
         return data (not empty).
   - [ ] No `row-level security` errors for ~15 min of real traffic.

## Step C — Bootstrap the first platform Root Owner

Until now the single god-mode `ControlPlane__AdminToken` is the only platform access.
Before you can retire it you must mint a real Root Owner:

> **Enrol MFA *before* granting the role — verified in an end-to-end run.** Root Owner
> is **break-glass**: it requires MFA for **every** permission, including Low-risk
> *reads*. A session for a user with no MFA enrolled can never satisfy that gate (the
> server only marks a session MFA-satisfied for enrolled users), so the console has
> **no data at all** for them. The console now explains this and links to enrolment
> rather than showing empty sections, but the operational order still matters.

1. [ ] On the named operator account, **enrol MFA first** (Security page), then sign
       out and back in so the session is MFA-satisfied.
2. [ ] With the admin token, grant `platform-root-owner` to that account:
       `POST /api/platform/role-assignments` `{ userId, role: "platform-root-owner" }`.
3. [ ] Sign in as that operator and confirm `/platform` shows the full console (Overview
       populated, tenants listed — **not** an "additional verification required" banner)
       and `GET /api/platform/whoami` returns the role.
4. [ ] Verify a Root-Owner-only action (grant another platform role) works end to end
       with step-up.

## Step D — Retire the god-mode token (deliberate, after Step C)

Only once a Root Owner is proven:

- [ ] Move `ControlPlane__AdminToken` to **break-glass only** — remove it from routine
      use, store it sealed, and rotate it. It still bypasses both the tenant and
      platform authorization gates, so treat it like a root password: sealed, audited,
      rotated after any use.
- [ ] Confirm all routine platform operations go through named roles + step-up, not the
      token.

## Rollback

Use [rls-rollout.md §Rollback](./rls-rollout.md#rollback) verbatim. Key facts, restated
so they're not missed under pressure:

- ⛔ **Never roll back with `dotnet ef database update <earlier-migration>`.**
  `AddRowLevelSecurity` is migration 11 of 27, so reverting past it **drops the P2–P7
  tables and all their data**. Rolling back RLS never requires reverting a migration.
- `FORCE ROW LEVEL SECURITY` subjects a **non-superuser owner** to policies too, so
  "just repoint `DATABASE_URL` to the owner" does **not** restore access on its own.
- Un-gate in place with `ALTER TABLE … NO FORCE` (owner exempt — pair with repointing
  `DATABASE_URL` to the owner) or `DISABLE ROW LEVEL SECURITY` (every role exempt).
- **Run all 16 FORCE'd tables, not just P1's 10.** Un-gating only P1 leaves the 6 P3
  tables fail-closed: sign-in and the fleet recover so the incident looks resolved,
  while scoped authorization silently degrades. The full list is in rls-rollout.md.
- Done this way, rollback touches **no data** — it only relaxes policy enforcement.
- If reverting the app, redeploy the prior `main` (pre-merge).

## Post-cutover

- [ ] Rotate `app_runtime` passwords if DDL logging may have captured them.
- [ ] Confirm `RlsCoverageTests` + `SecurityRegressionTests` stay green in CI — the
      latter dynamically enumerates the live route table, so no `/api/tenants/*` or
      `/api/platform/*` endpoint can ship returning 2xx unauthenticated.
- [ ] `railway login` for the unattended log monitor (needs the operator's Railway
      credentials).
- [ ] Mark P1–P7 complete.

## Reference

- RLS mechanics + rollback: [rls-rollout.md](./rls-rollout.md), [ADR 0018](../adr/0018-row-level-security-tenant-context.md).
- P3 pre-merge RLS design: [../architecture/p3-rls-premerge.md](../architecture/p3-rls-premerge.md).
- Staging smoke: [rls-staging-smoke.md](./rls-staging-smoke.md).
