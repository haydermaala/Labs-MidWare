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

Prefer merging **after** the `DATABASE_URL` repoint is live, so the first boot on
the new schema already runs as `app_runtime`. If your platform couples the two,
land the merge and repoint back-to-back and watch the first boot closely.

## Rollback

RLS problems in production → restore isolation fast, no data change:

1. Repoint production `DATABASE_URL` back to the **owner** role (the owner is
   exempt from `FORCE RLS`, so all queries work again immediately).
2. If needed, drop enforcement without a schema rebuild — as the owner:
   ```sql
   ALTER TABLE gateways NO FORCE ROW LEVEL SECURITY;  -- repeat per tenant table,
   -- or roll the migration back: it has a Down() that drops every policy and
   -- NO FORCE / DISABLEs RLS on all tables.
   ```
3. Redeploy the prior `main` (pre-merge) if the app itself must be reverted.

Because rollback is a **repoint** (owner role bypasses RLS) it is near-instant and
does not touch data. This is why the owner connection is retained.

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
