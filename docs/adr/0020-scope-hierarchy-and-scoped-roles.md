# ADR 0020 — Scope hierarchy + scoped roles (P3)

**Status:** Accepted (scaffold — scope hierarchy). Implementation on
`feat/p3-scopes-roles` (branched off P2 `feat/p2-permission-engine`, which it
extends). Follows the program plan `docs/architecture/rbac-multitenancy-program.md`
(P3) and the locked decision **D4 — full hierarchy now**.

**Context.** Today a membership pins a user to a *tenant* with **one** role
(P2 made those roles a permission catalog + engine). Real laboratories are
structured — a tenant has **sites**, each with **laboratories**, each with
**departments**, and gateways/devices live at some node. Operators need to grant a
role at a *level* ("lab-admin for the Haematology lab") that applies to that node
and everything under it, with expiry and, for regulated actions, separation of
duty. None of that exists yet.

## Decision

### 1. A scope tree per tenant (this scaffold)
Scopes are the nodes of a tenant's org hierarchy, shallow → deep:
`Tenant (0) → Site (1) → Laboratory (2) → Department (3)` (`ScopeType`). A scope
may contain another only if it is **strictly shallower** (`Scopes.CanContain`), so
levels can be skipped — a laboratory can sit directly under the tenant when a
customer has no sites. Each tenant has exactly one **root** scope of type Tenant.

`ScopeTree` (built + validated from a tenant's flat `ScopeNode` set) provides the
core relation scoped authorization is defined against:

> **`Contains(S, R)`** — a grant at scope `S` covers scope `R` iff `S` is `R` or
> an ancestor of `R`.

Persisted as `scopes` (`ScopeEntity`): `Id`, `TenantId`, `Type`, `Name`,
`ParentId` (null at the root), and a materialized `Path` (`/root/site/lab`, self
included) for prefix descendant queries. Gateways/devices gain a `ScopeId` in a
later slice; until then they remain tenant-level.

### 2. Scoped role assignments (model + resolver done)
`role_assignments` (`RoleAssignmentEntity`): `Id`, `TenantId`, `UserId`, `Role`,
`ScopeId`, `GrantedByUserId`, `CreatedAt`, `ExpiresAt?`, `RevokedAt?`. A subject
may hold several assignments at different scopes; the effective grant at a target
scope is the union over the subject's active assignments whose scope `Contains`
the target — `RoleAssignments.EffectiveRolesAt(...)`. A tenant-root assignment is
therefore tenant-wide, matching today's single `memberships.role` (kept as the
root assignment during migration). Expiry (and `RevokedAt`) are honoured at
evaluation time. *Still to wire:* the write/grant API (with delegation limits,
§3), the memberships→root-assignment backfill, and consumption by the engine (§4).

### 3. Custom roles + delegation (model done)
Tenant-defined roles are a named set of permission keys (`custom_roles` +
`role_permissions`), resolved uniformly with the code-owned baseline roles by
`RoleGrants.Grants`. Delegation (`Delegation`): a grantor may only delegate a
permission that is `Delegable` in the catalog **and** that the grantor holds;
`Delegation.Allowed` is the enforceable ceiling on a custom role a grantor
defines. Scope limits (at or below the grantor's scope) come from the engine
(§4). *Still to wire:* the create-role/grant admin API, the plan-entitlement gate,
and the `RoleGrants`-fed engine path. Baseline `RolePermissions`/`LegacyCapability`
stay for now (custom grants are additive); the bridge is retired when this path
becomes the engine's sole source at merge time.

### 4. Scoped authorization engine (later slice)
`AuthorizationEngine` gains a scope: `Authorize(subject, permission, targetScope)`
allows iff some assignment grants the permission at a scope that `Contains`
`targetScope` (and the P2 step-up/approval gates pass). The P2 `RolePermissions`
matrix and `LegacyCapability` bridge are retired here. Behaviour is preserved for
tenants with only the root scope (every grant is tenant-wide, i.e. today).

### 5. Separation of duty (model done)
`sod_rules` (per-tenant, mutually-exclusive permission pairs). `SeparationOfDuty`
provides the static check (`StaticViolations` for reviews, `WouldViolate` to gate
a grant) and the dynamic check (`IsDistinctParty`: approver ≠ requester). *Still
to wire:* enforcing `WouldViolate` in the grant path and `IsDistinctParty` in the
approval flow that the P2 `RequiresApproval` flag marks.

## Consequences

- **+** Grants map to how labs are actually organised; one assignment at a site
  cascades to its labs/departments via `Contains`.
- **+** The containment relation is a small, well-tested primitive; the engine,
  RLS descendant policies, and the admin UI all build on it.
- **−** More moving parts (a tree to keep valid, path maintenance on move/rename).
  Mitigated by validating on write (`ScopeTree.Build`) and the materialized path.
- **Migration:** existing memberships become tenant-root assignments, so no access
  changes when hierarchy is introduced.

## Integration notes

- **RLS (P1):** `scopes` and `role_assignments` are tenant-owned; when P1 and P3
  land together they need the `"TenantId" = current_setting('app.tenant_id')`
  policy and inclusion in the migration-gate test (`RlsCoverageTests`). Descendant
  visibility uses the `Path` prefix.
- **P2:** this branch is off P2; the scoped engine (§4) supersedes P2's
  tenant-only `Forbidden`/`RolePermissions`. Merge order: P1 → P2 → P3.

## Not in this scaffold (deferred)

- `role_assignments` + expiry (§2), custom roles + delegation (§3), the
  scope-aware engine (§4), and SoD (§5) — subsequent P3 slices.
- Attaching gateways/devices to a scope (`GatewayEntity.ScopeId`) and the
  scope-management admin surface (P4).

## Alternatives considered

- **Fixed 4-level nesting (no skipping):** rejected — many customers have no
  "site" tier; strictly-shallower containment is more faithful and no more complex.
- **Adjacency-only (no materialized path):** viable, but the `Path` makes
  descendant reads and RLS a cheap prefix match instead of recursive CTEs.
