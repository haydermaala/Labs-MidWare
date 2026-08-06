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

## Already verified on staging (2026-07-28/29)

Recorded so the cutover starts from evidence, not intent. All against the full P1–P7
stack on staging, as the least-privileged `app_runtime` role:

- **RLS genuinely enforces** — at the DB layer, not just the API: 16 FORCE'd tables,
  21 policies; `gateways`/`scopes`/`memberships`/`subscriptions`/`audit` all return
  **0 rows with no tenant GUC**; bound to a tenant, only that tenant's rows; a
  cross-tenant `INSERT` is refused.
- **The `AddTenantStatus` backfill works under FORCE RLS** — a tenant created before P7
  existed came back with `status: "Active"`, proving the `NO FORCE`/`FORCE` bracket.
- **Device plane** — enroll writes a credential; heartbeat/telemetry/config return 204
  and the heartbeat **persists** (`LastSeenAt` set); a wrong credential returns 401.
- **`invitations_token_auth`** — fail-closed with no GUC, blind to a wrong hash, reveals
  exactly the one matching invitation with no tenant GUC, leaks no others.
- **Full §10.3 offboarding pipeline** — distinct MFA-satisfied approver (SoD), archive
  refused during cooling-off (409), then archive succeeded: **5 gateways decommissioned,
  5 credentials revoked**, completion certificate written.
- **Break-glass MFA** — Root Owner without MFA gets 403 `{"stepUp":true}`; after
  enrolment the same read returns 200.
- **Rollback rehearsed** — un-gate all 16 → owner reads everything, 21 policies intact,
  row counts identical; re-FORCE → fail-closed restored.

Not yet verified: a business-day soak (everything above was burst testing), and the
console UI under a live authenticated platform login.

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
>
> **Staffing: the two-party checks need TWO accounts, and one cannot be the admin
> token.** Support-access approval and offboarding approval both enforce
> `SeparationOfDuty.IsDistinctParty`, so the approver must differ from the requester.
> The god-mode token authenticates as the single synthetic principal `platform-admin`,
> so it can request *or* approve but never both. Provision **two named,
> MFA-enrolled operator accounts** before starting Step A, or these items cannot be
> performed at all.

> ### ⚠️ Two checks below are false-negatives by construction — read this
>
> **A "green" P3 scopes/SoD check does not prove the P3 tables are readable.** If the
> `scopes`/`role_assignments` tables fail closed, `EffectiveRolesAt` simply returns no
> scoped roles and the caller falls back to their flat membership role — so scoped
> grants silently stop applying while everything *looks* normal. Verify positively:
> confirm a role granted at a **child scope** actually takes effect there, and confirm
> `SELECT count(*) FROM scopes` is **non-zero** for a tenant that has them.
>
> **A "green" billing check does not prove `subscriptions` is readable.** A fail-closed
> `subscriptions` read is indistinguishable from "no subscription": `EntitlementsFor`
> falls back to **Trial** and returns **200**. Every paid tenant would be silently
> downgraded — enrollments start failing at 2 gateways with 402, paid features vanish,
> and offboarding retention is computed from the wrong plan. Verify positively: for a
> tenant you know is on a paid plan, confirm `GET /api/tenants/{id}/billing` reports
> **that plan**, not `trial`.

### When is the soak "clean"? (the exit criteria)

The checks above are a *smoke test* — they run in minutes. The soak is the separate
question of whether the stack stays healthy across a **business day of real traffic**.
Do not treat a green smoke test as a completed soak.

Re-run these at the end of the window and compare against the baseline taken at the
start. All five must hold:

| # | Check | Clean means |
|---|-------|-------------|
| 1 | `GET /health/ready` | `"status":"ready"` **and** `"database":"postgres"` — the provider field is what catches a silent in-memory fallback |
| 2 | `scripts/staging-smoke.sh` (with a `TENANT_ID` that has data) | exit **0** — it now fails on empty reads, which is the RLS fail-closed signature |
| 3 | Log scan | **zero** occurrences of `row-level security`, `permission denied`, `42501`, `Unhandled`, `Exception` |
| 4 | RLS still armed | `SELECT count(*) … WHERE relforcerowsecurity` returns **16** |
| 5 | Deployed commit | still the intended SHA — no unnoticed redeploy or rollback |

If any check fails, the soak restarts after the fix; a partial soak proves nothing.

> **Baseline taken 2026-07-29T13:29Z** on `978fda8`: ready/postgres, 3 tenants
> (1 active / 1 archived / 1 suspended), unauthenticated platform read 401, and zero
> errors of every class above.

## Step B — Production cutover

In the maintenance window, following [rls-rollout.md Step 5](./rls-rollout.md#step-5--production-cutover):

> ### ⛔ MERGE FIRST, THEN REPOINT — never the other way round
>
> An earlier version of this runbook said to repoint `DATABASE_URL` → `app_runtime`
> **before** merging. **That takes production down before the cutover even starts.**
>
> Verified against the real `origin/main`: the pre-merge build has **no**
> `MIGRATION_DATABASE_URL` support (zero references in `DatabaseConfig.cs`) and **no**
> `TenantScope` at all. It calls `SchemaBootstrap.Apply(db)` on boot over the single
> `DATABASE_URL` — and `scripts/provision-app-runtime.sh` does
> `REVOKE ALL ON TABLE "__EFMigrationsHistory" FROM app_runtime`. So the moment
> `DATABASE_URL` points at `app_runtime` while the old image is still deployed, that
> image boots, tries to touch the migrations-history table, and **crash-loops**.
>
> The new build is the first one that can run as `app_runtime` (it has the
> migration/runtime split and binds the GUCs). So it must be deployed *first*.

1. [ ] **Merge the stack to `main`** (#76 — it now contains P6 and P7; see
       "The stacked branches"). Railway deploys. `DATABASE_URL` is still the **owner**
       at this point, which is correct and safe: the owner is a superuser, so it
       bypasses `FORCE` while the new code is already binding tenant GUCs.
       On boot the owner runs the full migration chain (both RLS migrations + the P7
       columns).
2. [ ] Verify the new build is healthy **before** touching the connection:
   - [ ] `/health/ready` green; the deploy is `SUCCESS`, not restarting.
   - [ ] Sign in; the fleet loads; a gateway heartbeat succeeds.
3. [ ] **Now repoint production `DATABASE_URL` → `app_runtime`** and let the service
       restart. This is the moment RLS actually starts enforcing, because
       `app_runtime` is the only role subject to `FORCE`.
4. [ ] Immediately verify:
   - [ ] `/health/ready` green.
   - [ ] Sign in; fleet loads; a gateway heartbeat succeeds; billing/audit reads
         return data (**not empty** — an empty list here is RLS failing closed).
   - [ ] No `row-level security` errors for ~15 min of real traffic.
   - [ ] Optional, strongest check: run `scripts/verify-rls.sql` as `app_runtime`
         against production (read-only apart from the two INSERTs it rolls back).

If step 4 goes wrong, the fastest recovery is repointing `DATABASE_URL` back to the
owner — see Rollback. Note that this ordering means the app is **never** running
against a connection it cannot use.

### If the migration chain fails part-way

The most likely way this cutover actually breaks. EF runs each migration in its own
transaction, so a mid-chain failure leaves the database **partially migrated**: the
migrations before the failure are applied and recorded, the failing one is rolled back,
and the rest never ran.

Do **not** "just redeploy" until you know which migration failed.

1. [ ] Find the boundary — the last recorded migration:
   ```sql
   SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 5;
   ```
2. [ ] Read the actual error in the deploy logs. `permission denied` means the migration
       ran on the wrong connection — check `MIGRATION_DATABASE_URL` is set and is the
       **owner**.
3. [ ] Decide by where it stopped:
   - **Before `AddRowLevelSecurity`** — no RLS is active yet. The app is unaffected;
     fix the cause and redeploy. `Database.Migrate()` resumes from the boundary.
   - **After either RLS migration** — RLS is partially armed. **Do not leave it here.**
     Either fix and redeploy to complete the chain, or un-gate (Rollback below) and
     re-arm with `scripts/rls-enable.sql` once resolved.
4. [ ] Re-run the arming check afterwards — it must report **16**:
   ```sql
   SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public' AND c.relforcerowsecurity;
   ```

Because `DATABASE_URL` is still the owner at this point (Step B.1 merges *before*
repointing), a partially-migrated database keeps serving — the owner is a superuser and
bypasses whatever FORCE did get applied. That is the main reason for this ordering.

## Step C — Bootstrap the first platform Root Owner

Until now the single god-mode `ControlPlane__AdminToken` is the only platform access.
Before you can retire it you must mint a real Root Owner:

> **Enrol MFA *before* granting the role — verified in an end-to-end run.** Root Owner
> is **break-glass**: it requires MFA for **every** permission, including Low-risk
> *reads*. A session for a user with no MFA enrolled can never satisfy that gate (the
> server only marks a session MFA-satisfied for enrolled users), so the console has
> **no data at all** for them. The console now explains this and links to enrolment
> rather than showing empty sections, but the operational order still matters.

> **Getting the `userId`.** There is no email→user lookup endpoint, so you cannot
> resolve one from the console. Either capture the `id` from the `POST /api/platform/users`
> response when the account is created (Root-Owner-gated, `platform.user.create` —
> prefer this over the legacy `POST /api/admin/users`, which is god-mode-token-only and
> is being retired), or read it once as the DB owner:
> `SELECT "Id" FROM users WHERE "Email" = lower('operator@example.com');`
>
> **The console will appear to go blank every 10 minutes.** Root Owner demands MFA
> *and* fresh auth for every permission, and the freshness window is
> `AuthService.StepUpWindow` = **10 minutes**. After it lapses, platform reads return
> 403 `{"stepUp":true}` until you re-authenticate. By design — expect it, and do Root
> Owner work in short focused bursts.

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

> **This is the single source of truth for rollback.** `rls-rollout.md` holds the
> mechanics and `rls-staging-smoke.md` covers verification only; if any of them appear
> to disagree, follow the order here. Ranked, safest first:
>
> 1. **Repoint `DATABASE_URL` → owner.** One variable change, no SQL, no data touched.
>    Works because Railway's owner is a superuser and bypasses `FORCE`. **Needs no
>    re-arm.** This resolves nearly every RLS incident.
> 2. **In-place un-gate** (`NO FORCE` / `DISABLE` on all 16 tables) — for a
>    non-superuser owner, or to restore the runtime role without repointing.
>    **Requires re-arming with `scripts/rls-enable.sql` afterwards.**
> 3. **Revert the app** — only after (1), and only if the new build is itself faulty.
>
> ⛔ Never `dotnet ef database update <earlier-migration>`; it drops the P2–P7 tables.

Use [rls-rollout.md §Rollback](./rls-rollout.md#rollback) verbatim. Key facts, restated
so they're not missed under pressure:

- ✅ **Fastest path on this deployment: repoint `DATABASE_URL` back to the owner.**
  Railway's managed `postgres` role owns the tables and is a genuine **superuser**
  (`rolsuper=t`, `rolbypassrls=t`, measured on staging 2026-07-28), and superusers
  bypass `FORCE` — so that single variable change restores access with no SQL.
  Re-check `SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname=current_user;`
  before relying on it, and repoint back to `app_runtime` after the incident.
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
- 🔁 **If you used the un-gate (`NO FORCE`/`DISABLE`), you must re-arm.** Nothing
  restores RLS automatically — both RLS migrations are already recorded, so
  `Database.Migrate()` skips them forever and every later deploy ratifies the
  un-gated state, with no error and no failing test. Before `DATABASE_URL` goes back
  to `app_runtime`, run `psql "$MIGRATION_DATABASE_URL" -f scripts/rls-enable.sql`
  as the owner and confirm it reports **16 forced tables / 21 policies**.
  (Not needed if you rolled back by repointing to the owner — that changes no SQL.)
- Done this way, rollback touches **no data** — it only relaxes policy enforcement.
- ⛔ **Reverting the app is only safe once `DATABASE_URL` points at the owner.**
  Pre-merge `main` has no migration/runtime split — it runs `SchemaBootstrap.Apply()`
  over whatever `DATABASE_URL` is, and `app_runtime` is `REVOKE`d on
  `__EFMigrationsHistory`. Redeploying it while `DATABASE_URL` is still `app_runtime`
  **crash-loops production**. Order: repoint to the owner **first**, confirm healthy,
  *then* redeploy the prior `main` if you still need to.
- In most incidents you will not need to revert the app at all: repointing
  `DATABASE_URL` to the owner restores service on its own, because the owner is a
  superuser and bypasses `FORCE`.

## Post-cutover

- [ ] **Confirm RLS is armed** (the one assertion that is safe read-only against
      production, and the only thing that would catch an un-re-armed database):
      ```sql
      SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
       WHERE n.nspname = 'public' AND c.relforcerowsecurity;  -- must be 16
      ```
      Re-run this after **any** incident that touched RLS.
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
