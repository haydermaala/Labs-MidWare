# Row-Level Security rollout runbook

Staged enablement of PostgreSQL Row-Level Security (RLS) for the control-plane
database — the single riskiest change in the multi-tenancy program. Design and
rationale: [ADR 0018](../adr/0018-row-level-security-tenant-context.md). This is
the operational procedure for the branch `feat/p1-rls-foundation`.

**Why this is careful.** The RLS migration turns on `FORCE ROW LEVEL SECURITY`.
Once the runtime connects as the least-privilege `app_runtime` role, any tenant
query with no context set returns **zero rows** (reads) or is **rejected** by
`WITH CHECK` (writes). If a code path that touches the database were missing its
scope, it would fail — so we prove the whole app first in staging, in shadow,
before the production cutover. Merging the migration to `main` **is** the
production cutover (Railway runs `Database.Migrate()` on deploy), so the merge is
the very last step, gated on everything below.

## Preconditions

- [ ] All app-side scoping is merged/ready on the branch: `EfControlPlaneStore`,
      `BillingService`, `MembershipService`, `AuthService` (ADR 0018 §2, §6–§8).
      Every `IDbContextFactory` consumer opens a scope; `Program.cs` only
      migrates + health-checks.
- [ ] The migration/runtime **connection split** is in place (already done):
      `Program.cs` migrates via `MIGRATION_DATABASE_URL` (owner role) and serves
      runtime via `DATABASE_URL` (`app_runtime`). See
      `DatabaseConfig.ResolveMigrationConnectionString`.
- [ ] `scripts/restore-drill.sh` has been run successfully against a **production
      backup** — a backup you have never restored is not a backup.
- [ ] Backups verified current for both staging and production.
- [ ] A maintenance window is agreed for the production step (brief; the cutover
      is a config repoint + deploy, seconds of restart).

## Roles

| Role | Attributes | Used for |
|---|---|---|
| owner (existing, e.g. the Railway-provisioned superuser/owner) | owns tables, DDL | migrations (`MIGRATION_DATABASE_URL`) |
| `app_runtime` | `LOGIN NOSUPERUSER NOBYPASSRLS`, owns nothing, DML only | runtime (`DATABASE_URL`) |

`app_runtime` is created out-of-band (not by a migration — the migration role
lacks `CREATEROLE`) with `scripts/provision-app-runtime.sh`.

---

## Step 1 — Staging: provision the runtime role

Run against **staging** first. Supply the password at run time (never commit it):

```bash
ADMIN_DATABASE_URL='postgres://<owner>:<pw>@<staging-host>:5432/<db>' \
APP_RUNTIME_PASSWORD='<strong-secret-for-staging>' \
OWNER_ROLE='<owner-role-that-runs-migrations>' \
scripts/provision-app-runtime.sh
```

The script is idempotent and prints a verification block. Confirm:
`can_login=t`, `is_superuser=f`, `bypasses_rls=f`, `tables_owned_must_be_zero=0`.

## Step 2 — Staging: split the connections and deploy the migration

On the staging service, set two variables:

- `MIGRATION_DATABASE_URL` → the **owner** connection (DDL rights).
- `DATABASE_URL` → the **`app_runtime`** connection (the password from Step 1).

Deploy the branch to staging. On boot, `SchemaBootstrap.Apply` runs
`Database.Migrate()` over `MIGRATION_DATABASE_URL`, applying
`AddDeviceCredentialTenantId` then `AddRowLevelSecurity` (ENABLE + FORCE RLS +
the tenant, device-auth, platform, and self/token policies). Runtime traffic then
flows as `app_runtime`.

Sanity check immediately after deploy:

- [ ] `/health/ready` is green (proves the runtime role can reach the DB).
- [ ] Sign in as an existing user (exercises `AuthService` under the platform
      audit sentinel — the path that would break first if unscoped).

## Step 3 — Staging: shadow soak

Drive every surface and watch for RLS symptoms. A missed scope shows up as either
a `new row violates row-level security policy` error (writes) or an unexpectedly
**empty** result (reads, fail-closed).

- [ ] Run `scripts/smoke.sh` against staging.
- [ ] Exercise each flow manually / via QA: login, signup, logout, MFA
      enable/verify/recover, tenant list + settings, gateway enroll (device
      plane), heartbeat/telemetry/config fetch, config publish, member invite +
      **accept**, role change, remove, billing entitlements + webhook, audit view,
      the `me/memberships` switcher.
- [ ] Watch application logs for `row-level security` / 500s and for endpoints
      returning empty where data is expected. Optionally raise DB logging on the
      staging instance (`log_min_error_statement = error`) to capture the exact
      statement behind any policy violation.
- [ ] Soak for an agreed period (e.g. 24–48h) with real staging usage. Zero RLS
      errors and no empty-result regressions is the gate to proceed.

Any failure → fix the missing scope on the branch, redeploy staging, restart the
soak. Do **not** proceed to production with open RLS symptoms.

## Step 4 — Production: provision + prepare (no cutover yet)

- [ ] Re-run `scripts/restore-drill.sh` against the **latest** production backup.
- [ ] Provision the role on **production** (a distinct, strong password —
      separate credentials per environment):

```bash
ADMIN_DATABASE_URL='postgres://<owner>:<pw>@<prod-host>:5432/<db>' \
APP_RUNTIME_PASSWORD='<strong-secret-for-prod>' \
OWNER_ROLE='<owner-role>' \
scripts/provision-app-runtime.sh
```

- [ ] Set `MIGRATION_DATABASE_URL` (owner) on the production service **now**, so
      it is present before the migration lands. Do **not** repoint `DATABASE_URL`
      yet.

## Step 5 — Production cutover

In the maintenance window:

1. [ ] Repoint the production `DATABASE_URL` → `app_runtime`.
2. [ ] Merge `feat/p1-rls-foundation` → `main`. Railway deploys; on boot the
       migration runs as the owner (`MIGRATION_DATABASE_URL`) and enables FORCE
       RLS, and runtime serves as `app_runtime`.
3. [ ] Immediately verify:
   - [ ] `/health/ready` green.
   - [ ] Sign in; load the fleet; a gateway heartbeat succeeds; a billing/audit
         read returns data (not empty).
   - [ ] No `row-level security` errors in logs for ~15 minutes of real traffic.

> ⛔ **Corrected ordering — merge first, then repoint.** This section previously said
> to prefer repointing `DATABASE_URL` *before* merging. That is wrong and takes
> production down: the pre-merge image has no `MIGRATION_DATABASE_URL` support and no
> `TenantScope`, it runs `SchemaBootstrap.Apply()` over the single `DATABASE_URL`, and
> `provision-app-runtime.sh` REVOKEs `__EFMigrationsHistory` from `app_runtime` — so
> the old image crash-loops the instant `DATABASE_URL` becomes `app_runtime`.
>
> The new build is the first that can run as `app_runtime`. Deploy it first (the owner
> connection is safe meanwhile — it is a superuser and bypasses `FORCE`), confirm
> healthy, and only then repoint. See
> [p1-p7-production-cutover.md Step B](./p1-p7-production-cutover.md#step-b--production-cutover)
> for the full ordered sequence.

## Rollback

RLS problems in production → restore access fast, no data change. **Read this
carefully — the naive "just repoint to the owner" does not work under `FORCE`.**

Verified against `postgres:16`: `FORCE ROW LEVEL SECURITY` subjects the table
**owner** to the policies too, so a **non-superuser owner** with no tenant context
also sees **zero rows**. Only a **superuser** or a `BYPASSRLS` role is exempt from
`FORCE`.

> ### ✅ On THIS deployment the fastest rollback is a single env-var change
>
> Measured on Railway staging (2026-07-28): the managed `postgres` role that owns all
> 26 tables reports **`rolsuper = t` and `rolbypassrls = t`** — it is a genuine
> superuser, not a plain owner. **Superusers bypass `FORCE` entirely**, so:
>
> **Repointing `DATABASE_URL` from `app_runtime` back to the owner restores access
> immediately, with no SQL at all.** That is one variable change and a restart — far
> faster and less error-prone at 2am than 16 `ALTER TABLE` statements.
>
> Confirm the assumption still holds before relying on it (providers change managed
> roles):
>
> ```sql
> SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user;
> ```
>
> If that ever returns `f`/`f`, fall back to the in-place un-gate below. Keep the
> un-gate documented regardless: it is the correct procedure for a non-superuser
> owner, and it is what you need if you must restore access **for the runtime role**
> without repointing at all. Do not *run* production on the superuser afterwards —
> repoint back to `app_runtime` once the incident is resolved.

> ### ⛔ DO NOT roll back with `dotnet ef database update`
>
> An earlier version of this runbook told you to run
> `dotnet ef database update <migration-before-AddRowLevelSecurity>`. **That command
> is destructive and must not be used.** `AddRowLevelSecurity` is migration 11 of 27;
> reverting to before it runs the `Down()` of **every migration after it**, which
> **DROPS** the P2–P7 tables and all their data — permission definitions, scopes, role
> assignments, custom roles, SoD rules, approval requests, every platform table
> (roles, support grants, security events, offboard requests), plus the session
> step-up and tenant-lifecycle columns.
>
> Rolling back RLS **never requires reverting a migration.** RLS is enforced by table
> attributes and policies, which you relax in place with the SQL below. Use that.

**The un-gate is the rollback.** As the owner (`MIGRATION_DATABASE_URL`), relax
enforcement in place — no migration step, no schema change, no data touched:

```sql
-- ALL 16 FORCE'd tenant tables (P1's 10 + P3's 6). Verified against the live
-- schema: SELECT relname FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
-- WHERE n.nspname='public' AND c.relforcerowsecurity;
ALTER TABLE approval_requests  NO FORCE ROW LEVEL SECURITY;
ALTER TABLE audit              NO FORCE ROW LEVEL SECURITY;
ALTER TABLE billing_events     NO FORCE ROW LEVEL SECURITY;
ALTER TABLE bootstrap_tokens   NO FORCE ROW LEVEL SECURITY;
ALTER TABLE configs            NO FORCE ROW LEVEL SECURITY;
ALTER TABLE custom_roles       NO FORCE ROW LEVEL SECURITY;
ALTER TABLE device_credentials NO FORCE ROW LEVEL SECURITY;
ALTER TABLE gateways           NO FORCE ROW LEVEL SECURITY;
ALTER TABLE invitations        NO FORCE ROW LEVEL SECURITY;
ALTER TABLE memberships        NO FORCE ROW LEVEL SECURITY;
ALTER TABLE role_assignments   NO FORCE ROW LEVEL SECURITY;
ALTER TABLE role_permissions   NO FORCE ROW LEVEL SECURITY;
ALTER TABLE scopes             NO FORCE ROW LEVEL SECURITY;
ALTER TABLE sod_rules          NO FORCE ROW LEVEL SECURITY;
ALTER TABLE subscriptions      NO FORCE ROW LEVEL SECURITY;
ALTER TABLE tenants            NO FORCE ROW LEVEL SECURITY;
```

`NO FORCE` exempts the **owner** only, so it must be paired with repointing
`DATABASE_URL` to the owner. To restore access for the **runtime** role without
repointing, use `DISABLE ROW LEVEL SECURITY` on the same 16 tables instead — that
exempts every role.

**Partial rollback is worse than none.** Un-gating only P1's 10 tables leaves the 6
P3 tables (`scopes`, `role_assignments`, `sod_rules`, `custom_roles`,
`role_permissions`, `approval_requests`) still fail-closed. Sign-in and the fleet
appear to recover, so the incident looks resolved — but `ScopeService.Tree()` returns
null and scoped authorization silently degrades. **Always run all 16.**

Then, if the app itself must be reverted, redeploy the prior `main` (pre-merge).

The rollback above touches **no data** — it only relaxes policy enforcement — and is
why the owner (`MIGRATION_DATABASE_URL`) connection is retained. (If your provider
hands you a genuine **superuser** rather than a plain owner, repointing `DATABASE_URL`
to it *does* restore access immediately, since superusers bypass `FORCE` — also
verified — but do not run production on a superuser afterwards.)

## Post-cutover

- [ ] Rotate the `app_runtime` passwords if DDL logging may have captured them
      (see the note the script prints), or set them via `\password` (SCRAM).
- [ ] Confirm the migration-gate test (`RlsCoverageTests`) stays green in CI, so a
      future tenant table cannot ship without a policy.
- [ ] Mark P1 complete; the super-admin platform (broad cross-tenant reads behind
      `/platform-admin`) is P6 and layers named platform roles on top of this
      foundation.

## Reference

- Design + proofs: [ADR 0018](../adr/0018-row-level-security-tenant-context.md).
- Scope helpers: `TenantScope`, `DeviceScope`, `PlatformScope`, `UserScope`,
  `InvitationScope`.
- Scripts: `scripts/provision-app-runtime.sh`, `scripts/restore-drill.sh`,
  `scripts/smoke.sh`.
