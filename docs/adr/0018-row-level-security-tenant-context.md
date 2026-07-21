# ADR 0018 — Row-Level Security + tenant context (P1)

**Status:** Accepted (design). Implementation on `feat/p1-rls-foundation`; NOT
merged until the staged production rollout (§Rollout).

**Context.** LabConnect enforces tenant isolation only in the application layer
(`IControlPlaneStore` filters every query by `tenant_id`). That is a single line
of defense: one missing `WHERE tenant_id = …`, one raw query, or one IDOR and a
tenant sees another's data. The master prompt (§5.7) and OWASP Multi-Tenant
guidance require **database-level** isolation as defense-in-depth. The mechanism
is proven (see `docs/architecture/rbac-multitenancy-program.md` §"P1 mechanism —
PROVEN"): `FORCE RLS` + a deny-by-default policy keyed on
`current_setting('app.tenant_id', true)`, under a least-privilege runtime role.

## Decision

### 1. Least-privilege runtime DB role
The web app connects as **`app_runtime`** — `LOGIN`, `NOSUPERUSER`,
`NOBYPASSRLS`, not the owner of any table, granted only `SELECT/INSERT/UPDATE/
DELETE` on application tables. Migrations and backups use separate roles. The
role is created **out-of-band** (infra/psql), not in an EF migration (the
migration role lacks `CREATEROLE`); `DATABASE_URL` is repointed to it at rollout.

### 2. Tenant context via transaction-local GUCs
Each authenticated operation runs inside a transaction that first issues
`SET LOCAL app.tenant_id = '<verified>'` (also `app.user_id`, `app.membership_id`,
`app.support_grant_id`). The value comes **only** from the authenticated session's
verified membership — never a request-supplied id. `SET LOCAL` is
transaction-scoped, so pooled connections never leak context across requests.

**Enforcement shape for the current per-operation store.** Today
`EfControlPlaneStore` does `using var db = _factory.CreateDbContext()` per method
with no explicit transaction. `SET LOCAL` requires a transaction, so a small
`TenantScope` helper (`TenantScope.cs`) wraps each tenant-touching operation: it
`BEGIN`s a transaction and binds the GUC via
`set_config('app.tenant_id', <id>, true)` (the parameterised, function form of
`SET LOCAL`), then the operation runs and `Complete()` commits. It is a no-op
under the in-memory provider (tests), so behaviour there is unchanged.

*Implemented* (this branch): the tenant id is sourced from the `tenantId` the
store method already receives, which the endpoint layer has already authorised
against the caller's verified membership (`AuthorizedInTenant`) — so it is the
verified tenant, never a raw request field, and RLS becomes true
defense-in-depth beneath the app-layer `tenant_id` filters (which stay). The
scoped methods: `CreateTenant` (bound to the new tenant's id, satisfying the
`tenants` WITH CHECK), `RenameTenant`, `DeactivateTenant`/`ReactivateTenant`,
`IssueBootstrapToken`, `DecommissionGateway`, `GatewaysFor`, `PublishConfig`,
`AuditFor`.

*Two elevated paths remain* (deliberately unscoped, flagged in code, required
before FORCE RLS goes live — see §Rollout):
1. **Platform / cross-tenant reads** (`Tenants()`, `TenantExists`,
   `FindTenant`): enumerate across tenants, so they cannot run under a single
   tenant GUC — they need the P6 platform role (BYPASSRLS or a cross-tenant
   policy behind `/platform-admin`).
2. **Device-plane auth bootstrapping** (`Enroll`, `ValidateDeviceCredential`,
   `RecordHeartbeat`, `RecordTelemetry`, `TenantOfGateway`, `CurrentConfig`):
   the tenant is unknown until a bootstrap token / device credential is
   validated, but reading that secret is what RLS would block — needs a narrow
   by-PK+secret device-auth policy, after which the operation sets
   `app.tenant_id` to the resolved tenant for its writes.

A later `TenantContext` (ambient, set by request middleware) can additionally
bind `app.user_id`/`app.membership_id`/`app.support_grant_id` for user-scoped
policies and richer audit; not required for tenant isolation and deferred.

### 3. RLS policies per table
`ENABLE` + `FORCE ROW LEVEL SECURITY` on every tenant-owned table, each with one
deny-by-default `USING`/`WITH CHECK` policy:

EF maps entity properties to **PascalCase** columns (no snake_case convention),
so predicates must double-quote the identifiers (an unquoted `tenant_id` folds to
lower-case and misses the real `"TenantId"` column — a bug caught and fixed
during full-schema apply-verification, see §Verification):

| Table | Policy key |
|---|---|
| `gateways`, `bootstrap_tokens`, `configs`, `audit`, `memberships`, `invitations`, `subscriptions`, `billing_events` | `"TenantId" = current_setting('app.tenant_id', true)` |
| `device_credentials` (no `TenantId`; keyed by `"GatewayId"`) | `"GatewayId" IN (SELECT "Id" FROM gateways WHERE "TenantId" = current_setting('app.tenant_id', true))` |
| `tenants` (the tenant row itself) | `"Id" = current_setting('app.tenant_id', true)` |

Global/user-scoped tables (`users`, `user_sessions`, `user_tokens`,
`recovery_codes`) are **not** tenant-scoped and are excluded from tenant RLS;
they are protected by the app's user-id checks (and a future `app.user_id`
policy if warranted). `current_setting(…, true)` returns NULL when unset ⇒ the
row is invisible (fail-closed).

### 4. Migration-gate test
A test enumerates tenant-owned tables and fails if any lacks: a `tenant_id`
column (or a documented join-based policy like `device_credentials`), RLS
enabled + forced, an applicable policy, and a leading `tenant_id` index. New
tenant tables cannot ship without isolation.

### 5. Rollout (never a one-step prod change)
`restore-drill.sh` demonstrated on a prod backup → create `app_runtime` +
enable RLS in **staging** behind a flag → **shadow-log** any policy denials
(catches a missed `SET LOCAL`) → **canary** → repoint prod `DATABASE_URL` to
`app_runtime` and enable `FORCE RLS` in a maintenance window. Rollback: repoint
`DATABASE_URL` to the owner role + `NO FORCE ROW LEVEL SECURITY`. **The EF RLS
migration is not merged to `main` until this sequence completes** — Railway runs
`Database.Migrate()` on deploy, so a merge *is* the production cutover.

## Verification

Full-schema apply-verification (2026-07-21): the real EF migration script
(`dotnet ef migrations script --idempotent`, InitialCreate → AddRowLevelSecurity)
was applied to a throwaway `postgres:16`, then exercised as the least-privilege
`app_runtime` role. Confirmed against the **actual** schema:

- schema + RLS DDL apply cleanly (the initial lower-case `tenant_id` predicates
  were rejected — real columns are PascalCase — and were corrected to `"TenantId"`);
- **fail-closed** — no `app.tenant_id` ⇒ 0 rows on `gateways`/`tenants`/`device_credentials`;
- **tenant scoping** — `app.tenant_id='t1'` shows only t1's gateway, tenant row,
  and (via the gateway-join policy) only t1's device credential;
- **cross-tenant INSERT** of a `t2` gateway under t1 context is rejected by `WITH CHECK`.

`TenantScope`'s exact SQL was then proven as the least-privilege `app_runtime`
role: `set_config('app.tenant_id', 't1', true)` inside a transaction scopes the
following query to t1; on the **same** connection *between* transactions the GUC
is cleared ⇒ 0 rows (proves no context leaks across pooled connections — the
reason for `SET LOCAL`/local `set_config` over session `SET`); a second
transaction rebinds to t2 cleanly. This is precisely EF's per-operation
transaction lifecycle, so the helper binds correctly against real Postgres.

## Consequences

- **+** Real database-level tenant isolation; an app-layer bug can no longer leak
  cross-tenant data. Aligns with ASVS L2/L3 and OWASP Multi-Tenant.
- **+** Fail-closed: no tenant context ⇒ no rows, surfacing missed context loudly
  in staging shadow logs rather than silently leaking in prod.
- **−** Every tenant store method must open a `TenantScope` (transaction + GUCs);
  a missed one returns 0 rows (caught by shadow logging + tests, not a leak).
- **−** Operational: a second DB role to manage; migrations/backups run as
  distinct roles.
- **Risk:** enabling `FORCE RLS` + the least-priv role on the live DB is the
  single riskiest change in the program; mitigated entirely by the staged
  rollout above and an instant repoint/`NO FORCE` rollback.

## Alternatives considered

- **App-layer filters only (status quo):** rejected — single line of defense.
- **Session-level `SET` (not `SET LOCAL`):** rejected — leaks tenant context
  across pooled connections.
- **`tenant_id` on `device_credentials`:** viable (denormalize) but the
  join-based policy avoids a schema/backfill change and keeps the gateway the
  source of truth. Revisit if the subquery cost matters at scale.
