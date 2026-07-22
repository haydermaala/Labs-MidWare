# P3 RLS — pre-merge staging

Row-Level Security is a P1 concern (ADR 0018) that lives on `feat/p1-rls-foundation`.
The P3 branch (`feat/p3-scopes-roles`) was built off `main`/P2, which has **no RLS**,
so its new tables have no policies and its services do plain (unscoped) queries.
This document pre-stages everything needed so that at the **P1 → P2 → P3 merge** the
RLS work is mechanical and low-risk. Nothing here is wired on the P3 branch itself.

## What P3 adds that RLS must cover

Six new tenant-owned tables, plus one new column:

| Table | Tenant column | Policy shape |
|---|---|---|
| `scopes` | `"TenantId"` | plain tenant isolation |
| `role_assignments` | `"TenantId"` | plain tenant isolation |
| `sod_rules` | `"TenantId"` | plain tenant isolation |
| `custom_roles` | `"TenantId"` | plain tenant isolation |
| `role_permissions` | `"TenantId"` | plain tenant isolation |
| `approval_requests` | `"TenantId"` | plain tenant isolation |
| `gateways.ScopeId` (new column) | — | **no new policy** (see below) |

Every one of these tables is only ever accessed with a **known authorized-tenant
context** — the admin the endpoint already authorized in that tenant. None is
reached through a device/token bootstrap path. So unlike P1's `device_credentials`
(join-self) or `bootstrap_tokens`/`invitations` (token-auth auxiliary policies),
they need only the **same single-table, deny-by-default isolation policy** as the
P1 tenant tables. No auxiliary policies.

`gateways` already has its P1 isolation policy; `ScopeId` is just another column
under it. A gateway's `ScopeId` is validated to belong to the same tenant at assign
time (app layer), and a cross-tenant scope id is unreachable anyway (a caller only
ever sees its own tenant's scopes). No policy change to `gateways`.

## The policy SQL (validated)

The ready statements are in **`scripts/p3-rls-policies.sql`** — a `DO` block that,
for each of the six tables, does `ENABLE` + `FORCE ROW LEVEL SECURITY` and creates a
`<table>_tenant_isolation` policy:

```sql
USING     ("TenantId" = current_setting('app.tenant_id', true))
WITH CHECK ("TenantId" = current_setting('app.tenant_id', true))
```

This matches P1's `AddRowLevelSecurity` pattern exactly: quoted PascalCase columns
(EF maps `"TenantId"`, tables lowercase), `current_setting(..., true)` → NULL when
unset so a query with no tenant context matches nothing (fails closed), and `FORCE`
so even a non-superuser owner is subject to it.

**Validated on `postgres:16`** (2026-07-22) against the real EF-generated schema, as
a least-privilege `NOSUPERUSER NOBYPASSRLS` role:

- tenant-scoped read (`app.tenant_id=t1`) → only t1's rows;
- no GUC → 0 rows (fail closed);
- cross-tenant INSERT (`guc=t1`, row `TenantId=t2`) → `ERROR: new row violates
  row-level security policy`;
- correct-tenant INSERT → succeeds.

### At merge

Turn `scripts/p3-rls-policies.sql` into the `Up()` of an `AddP3RowLevelSecurity` EF
migration (`migrationBuilder.Sql(...)`), timestamped **after** P1's
`AddRowLevelSecurity` and after the P3 schema migrations (`AddScopes` …
`AddApprovalRequests`, `AddGatewayScopeId`). `Down()` = `NO FORCE` + `DISABLE ROW
LEVEL SECURITY` + `DROP POLICY` per table (mirror P1's Down). Align the policy name
with P1's convention if it differs from `<table>_tenant_isolation`.

## Service wiring — the load-bearing part

Under `FORCE` RLS as `app_runtime`, every P3 query must run inside a transaction with
`app.tenant_id` bound, via the P1 `TenantScope` helper (no-op under the in-memory
provider). The P3 services currently do **plain** `IDbContextFactory` access and
must each be wrapped at merge — the tenant id is already known (the endpoint
authorized it), so this is the same treatment `EfControlPlaneStore` got in P1.

- **`ScopeService`** — `EnsureRoot` (bind the NEW root's tenant for the `WITH CHECK`),
  `CreateChild`, `Tree`, `List`. All tenant-scoped.
- **`RoleGrantService`** — `Grant`, `Revoke`, `AssignmentsFor`, `ActiveAssignmentsFor`,
  `CreateCustomRole`, `CustomGrantsFor`, `CustomRolesFor` (and the internal
  `PermissionsOfRole`/`HeldPermissions`/`ActiveRules`/`IsKnownRole` reads, which run
  inside those methods' contexts). All tenant-scoped.
- **`ApprovalService`** — `Create`, `Find`, `Approve`/`Reject`/`Decide`, `Pending`.
  All tenant-scoped.
- **`Forbidden` / `RootScopeContext`** (Program.cs) — calls
  `scopeService.Tree` + `roleGrants.ActiveAssignmentsFor` + `CustomGrantsFor` at
  request time; covered automatically once those service methods are wrapped.

### ⚠ `MembershipAssignmentBackfill` is cross-tenant

`MembershipAssignmentBackfill.Apply` iterates **all** tenants' memberships/scopes/
role_assignments — it cannot run as tenant-scoped `app_runtime` under FORCE RLS
(it would see nothing / fail its writes). It is a one-time startup reconcile, so run
it under the **migration/owner connection** (the superuser that P1's `SchemaBootstrap`
already uses, which bypasses FORCE), *not* the runtime `app_runtime` factory. Concretely:
in the Program.cs startup Postgres block, pass the migration-connection `AppDbContext`
to `MembershipAssignmentBackfill.Apply(...)`, alongside `SchemaBootstrap`/
`PermissionCatalogSync`. (This is the one behavioural wiring change the merge needs
beyond mechanical `TenantScope` wraps.)

## Migration-gate test (`RlsCoverageTests`)

P1's `RlsCoverageTests` forces every mapped table into a bucket. After the merge the
six P3 tables are **tenant-owned** (they have `"TenantId"`) → each must appear in the
combined RLS policy set (`AddP3RowLevelSecurity.Policies`, or however P1's
`AddRowLevelSecurity.Policies` list is extended). Add them to the tenant-owned
expectation so the gate fails if any P3 table ships without a policy.

- Tenant-owned (need a policy): `scopes`, `role_assignments`, `sod_rules`,
  `custom_roles`, `role_permissions`, `approval_requests`.
- Global (no `TenantId`, must **not** have a tenant policy): `permission_definitions`
  (the P2 catalog mirror) — classified at the **P2** merge, noted here for completeness.
- `gateways` stays in its existing P1 bucket; `ScopeId` does not change it.

## Merge checklist

1. Land P1, then P2, then P3 onto the integration branch.
2. Add `AddP3RowLevelSecurity` migration from `scripts/p3-rls-policies.sql` (+ `Down`).
3. Wrap the P3 service methods above in `TenantScope`.
4. Move `MembershipAssignmentBackfill.Apply` onto the migration/owner connection.
5. Extend `RlsCoverageTests` with the six tenant-owned P3 tables.
6. Ensure `app_runtime` has grants on the new tables — P1's `provision-app-runtime.sh`
   uses `ALTER DEFAULT PRIVILEGES`, so tables created after the role are auto-granted;
   verify on the staging DB that the P3 tables did land with the expected grants.
7. Re-run the full apply-verification on a throwaway `postgres:16` (as in P1) over the
   whole chain, then the staged rollout.
