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
   `RecordHeartbeat`, `RecordTelemetry`, `CurrentConfig`): the tenant is unknown
   until a bootstrap token / device credential is validated, but reading that
   secret is what RLS would block. **Resolved and implemented (§6):** a
   `DeviceScope` binds the device-auth GUCs so the policy reveals only the
   presented secret's row; `Enroll` then binds the resolved tenant for its
   writes, and `ValidateDeviceCredential` returns the tenant which the endpoints
   thread into the now-tenant-scoped steady-state ops. `device_credentials`
   carries a denormalized `TenantId` (single-table policies, no recursion).

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
| `gateways`, `bootstrap_tokens`, `configs`, `audit`, `memberships`, `invitations`, `subscriptions`, `billing_events`, `device_credentials` | `"TenantId" = current_setting('app.tenant_id', true)` |
| `tenants` (the tenant row itself) | `"Id" = current_setting('app.tenant_id', true)` |

> **`device_credentials` carries a denormalized `TenantId`** (set at enrollment)
> rather than a `gateway_id`→`gateways` join. This is required by the device-auth
> design (§6): a join-based policy makes `device_credentials` reference `gateways`,
> and once `gateways` also gets a device-auth policy referencing `device_credentials`,
> Postgres raises *"infinite recursion detected in policy"* (reproduced). A
> single-table predicate on every table avoids the cycle, and the credential row
> then carries the tenant so device auth can resolve it without reading `gateways`.

Global/user-scoped tables (`users`, `user_sessions`, `user_tokens`,
`recovery_codes`) are **not** tenant-scoped and are excluded from tenant RLS;
they are protected by the app's user-id checks (and a future `app.user_id`
policy if warranted). `current_setting(…, true)` returns NULL when unset ⇒ the
row is invisible (fail-closed).

### 4. Migration-gate test
*Implemented* (`RlsCoverageTests`): the gate builds the model with the relational
provider and forces every mapped table into exactly one bucket — carries a
`TenantId` column, or is a documented join/self table (`device_credentials`,
`tenants`), or is an explicitly-listed global/user table (`users`,
`user_sessions`, `user_tokens`, `recovery_codes`). The first two must appear in
the `AddRowLevelSecurity` policy set; the last must not (and must not carry a
`TenantId`). Any table in none of the buckets is "unclassified" and fails the
build — a new tenant table cannot ship without a deliberate isolation decision.
Companion assertions check no policy targets an unknown or global table, and lock
the P1 coverage set at the ten known tenant tables. The gate had its teeth proven
(removing any table's policy fails it). EF already emits the leading `TenantId`
indexes (`AppDbContext.OnModelCreating`).

### 5. Rollout (never a one-step prod change)
Executable procedure: **[docs/operations/rls-rollout.md](../operations/rls-rollout.md)**;
role provisioning: `scripts/provision-app-runtime.sh`. Migrations need DDL/owner
rights the runtime role lacks, so the app migrates via a separate
`MIGRATION_DATABASE_URL` (owner) and serves runtime via `DATABASE_URL`
(`app_runtime`) — `DatabaseConfig.ResolveMigrationConnectionString`, falling back
to the runtime connection when unset so single-role deploys are unchanged.


`restore-drill.sh` demonstrated on a prod backup → create `app_runtime` +
enable RLS in **staging** behind a flag → **shadow-log** any policy denials
(catches a missed `SET LOCAL`) → **canary** → repoint prod `DATABASE_URL` to
`app_runtime` and enable `FORCE RLS` in a maintenance window. Rollback: repoint
`DATABASE_URL` to the owner role + `NO FORCE ROW LEVEL SECURITY`. **The EF RLS
migration is not merged to `main` until this sequence completes** — Railway runs
`Database.Migrate()` on deploy, so a merge *is* the production cutover.

### 6. Device-plane authentication under RLS
Gateways authenticate with a secret (a bootstrap token at enrollment, then a
device credential), presented *before* any tenant is known — but reading the
secret to validate it is what tenant RLS blocks. Two **single-table** device-auth
policies (permissive, OR-combined with the tenant policies) resolve this by
revealing only the row whose secret the caller proves possession of, keyed on
transaction-local GUCs:

| Table | Device-auth policy | GUCs set by |
|---|---|---|
| `bootstrap_tokens` | `"Token" = current_setting('app.device_token', true)` | `Enroll` |
| `device_credentials` | `"GatewayId" = current_setting('app.device_gateway', true) AND "Credential" = current_setting('app.device_credential', true)` | `ValidateDeviceCredential` |

The credential policy requires the **credential** (not just the guessable gateway
id) to reveal the row, so it never discloses a stored secret to a caller who
knows only a gateway id; the app-side constant-time compare stays as a second
layer. Because `device_credentials` now carries `TenantId`, the revealed row
yields the tenant directly — so the flow is:

1. **Enroll**: `set app.device_token` → read+consume the token → `set app.tenant_id`
   to the token's tenant → insert gateway + credential (both under the tenant
   `WITH CHECK`). No `gateways`/`configs` device-auth policy needed.
2. **Steady-state** (`RecordHeartbeat`/`RecordTelemetry`/`CurrentConfig`):
   `ValidateDeviceCredential` resolves the tenant from the credential row, then
   the operation runs **tenant-scoped** under the ordinary tenant policies.

Cross-table device-auth policies (e.g. a `gateways` policy doing
`EXISTS(SELECT FROM device_credentials …)`) were **rejected**: with the
join-based `device_credentials` policy they form a cycle and Postgres raises
*"infinite recursion detected in policy for relation gateways"* (reproduced
against `postgres:16`). The denormalized-`TenantId` design keeps every predicate
single-table.

Unset device GUCs ⇒ `current_setting(…, true)` is NULL ⇒ `= NULL` is false ⇒ no
rows: the device paths are fail-closed too.

### 7. Platform / cross-tenant registry reads
A few trusted server-side operations legitimately span tenants — the admin
tenant list (`GET /api/tenants`) and resolving tenant names across a user's
memberships. These read the tenant **registry** (id/name/active), not tenant
data, and cannot run under a single tenant GUC. A permissive policy on `tenants`
grants this, gated on a transaction-local flag:

```
CREATE POLICY tenants_platform_read ON tenants
  USING (current_setting('app.platform', true) = 'true');
```

`PlatformScope` sets the flag; only `Tenants()` opens it. **Single-tenant**
lookups (`TenantExists`, `FindTenant`) take a specific id and stay tenant-scoped
under the self-policy — the platform flag is never set for them (least
privilege). The policy is scoped to `tenants` **only**, so the flag grants no
cross-tenant access to actual tenant data (`gateways`/`configs`/`audit`/
`subscriptions`/…). The full super-admin cross-tenant surface — reading across
all tenants' operational data behind `/platform-admin` — is **P6**, via named
platform roles (with `BYPASSRLS` or their own broader policies) and step-up
auth; this P1 policy is deliberately just the registry read the existing app
already needs.

### 8. Membership & billing service scoping
`EfControlPlaneStore` is not the only DB-touching service. `BillingService` and
`MembershipService` (and their `IDbContextFactory` access) are scoped the same
way:

- **BillingService** — every method is tenant-scoped (`TenantScope`): the
  entitlement/subscription reads and `UpsertSubscription` take a `tenantId`, and
  `TryApplyProviderEvent` scopes to the event's verified `ev.TenantId` (the
  webhook carries its tenant; the replay check only needs this tenant's
  `billing_events`, with the global UNIQUE index as the cross-tenant backstop).
- **MembershipService** — most methods are tenant-scoped (`RoleIn`, `MembersOf`,
  `Grant`, `ChangeRole`, `RemoveMember`, `Invite`, `InvitationsFor`,
  `RevokeInvitation`). Two need auxiliary policies (both single-table, added to
  the `AuxiliaryPolicies` set):
  - `MembershipsFor(userId)` reads a user's memberships across every tenant, so
    it runs under a `UserScope` binding `app.user_id`; the `memberships_self_read`
    policy reveals only the caller's own rows.
  - `Accept(token)` finds an invitation by its hashed token before the tenant is
    known, so it runs under an `InvitationScope` binding
    `app.invitation_token_hash` (the `invitations_token_auth` policy), then binds
    the resolved tenant for the accept write — mirroring the bootstrap-token flow.

- **AuthService** operates on global tables (`users`, `user_sessions`,
  `user_tokens`, `recovery_codes`) which carry no tenant RLS — but its **audit
  trail** lives in the RLS-protected `audit` table under a `"platform"` sentinel
  tenant (`AuthService.PlatformAuditTenant`). So every method that writes (login,
  signup, logout, MFA, verification, reset) opens a `TenantScope` bound to that
  sentinel; read-only paths and the per-request `Authenticate()` (which touch no
  RLS table) stay unscoped. Because the scope wraps a transaction, **every path
  that calls `SaveChanges` must `Complete()`** — including the wrong-MFA-code
  paths, which persist a token consumption even though they write no audit row.

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

**Device-auth (§6) proven** as `app_runtime` against the denormalized schema:
the recursive cross-table variant was first reproduced failing (*"infinite
recursion detected in policy for relation gateways"*), then the single-table
design was exercised end-to-end — enroll (`app.device_token` reveals only the
presented token ⇒ resolves its tenant ⇒ inserts gateway + credential under that
tenant); credential validation (`app.device_gateway` + `app.device_credential`
reveal the credential row *with its TenantId*; wrong credential or a different
gateway's id ⇒ 0 rows); steady-state (tenant-scoped update sees only the
authenticated gateway). All device paths fail closed when the GUCs are unset.

After the store was wired, the **full real migration chain** (through the
`AddDeviceCredentialTenantId` column + backfill and the rebuilt
`AddRowLevelSecurity`) was applied to `postgres:16` and the exact SQL sequences
`EfControlPlaneStore` now issues — the enroll transaction, the credential lookup,
and the tenant-scoped heartbeat — were replayed as `app_runtime` and all
succeeded. 118 backend tests pass (device-auth is a no-op under the in-memory
provider, so behaviour there is unchanged).

**Platform policy (§7) proven** as `app_runtime`: with `app.platform='true'` a
read of `tenants` returns all rows (cross-tenant registry) while `gateways`
returns **0** (the flag grants no access to tenant data); with no flag and no
tenant context, `tenants` returns 0 (fail-closed); with `app.tenant_id='t1'`
only t1's registry row is visible (self-policy).

**Service policies (§8) proven** as `app_runtime`: `memberships_self_read` —
`app.user_id='u1'` reveals u1's memberships across both its tenants and no one
else's, 0 with no user context; `RoleIn` still sees a tenant's memberships under
`app.tenant_id`. `invitations_token_auth` — the hashed token reveals only the
matching invitation, after which binding its tenant lets the accept update +
membership insert succeed; a wrong hash reveals nothing. The **AuthService**
pattern is proven too: under `app.tenant_id='platform'` a global user insert and
the platform-sentinel audit insert both succeed; **without** the scope the same
audit insert is rejected by the policy (the failure the wiring prevents).

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
