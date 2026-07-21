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
with no explicit transaction. `SET LOCAL` requires a transaction, so we introduce
a `TenantContext` (ambient, set by request middleware from the verified session)
and a small `TenantScope` helper that every tenant-touching store method opens:
it begins a transaction and sets the GUCs before running the operation. Device
endpoints (gateway credential auth) set `app.tenant_id` to the gateway's tenant
after credential validation. Admin/platform operations set a dedicated
`app.tenant_id` per targeted tenant, or use a separate elevated policy path
(P6). This keeps RLS as **defense-in-depth**: the app-layer `tenant_id` filters
stay.

### 3. RLS policies per table
`ENABLE` + `FORCE ROW LEVEL SECURITY` on every tenant-owned table, each with one
deny-by-default `USING`/`WITH CHECK` policy:

| Table | Policy key |
|---|---|
| `gateways`, `bootstrap_tokens`, `configs`, `audit`, `memberships`, `invitations`, `subscriptions`, `billing_events` | `tenant_id = current_setting('app.tenant_id', true)` |
| `device_credentials` (no `tenant_id`; keyed by `gateway_id`) | `gateway_id IN (SELECT id FROM gateways WHERE tenant_id = current_setting('app.tenant_id', true))` |
| `tenants` (the tenant row itself) | `id = current_setting('app.tenant_id', true)` |

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
