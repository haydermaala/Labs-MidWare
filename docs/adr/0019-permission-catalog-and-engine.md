# ADR 0019 — Permission catalog + authorization engine (P2)

**Status:** Accepted (scaffold). Implementation on `feat/p2-permission-engine`.
Follows the multi-tenancy/RBAC program (`docs/architecture/rbac-multitenancy-program.md`, P2).

**Context.** Authorization today is scattered: each endpoint calls a coarse
`Roles.Can*` predicate (`CanManageFleet`, `CanManageUsers`, `CanManageTenant`,
`CanManageBilling`, `CanView`). There is no catalog of what actions exist, no
central decision point, no per-action metadata (risk, step-up, approval), and no
way to answer "why can/can't this user do X?". That blocks a tenant-admin app, an
access-review surface, step-up auth on high-risk actions, and an authZ regression
matrix (program §6, §8, §13, §19).

## Decision

### 1. A permission catalog
Every action is a versioned permission keyed `<domain>.<resource>.<action>`
(e.g. `fleet.gateway.decommission`) with governance metadata:
`Risk` (Low/Medium/High/Critical), `RequiresMfa`, `RequiresFreshAuth` (step-up),
`RequiresApproval`, and `Delegable`. The catalog lives in code
(`Permissions.cs`) as greppable static fields — the same discipline as `Roles` —
and is the single source of truth. A later migration seeds a
`permission_definitions` table from it for the admin UI and referential
integrity; the code catalog stays authoritative.

### 2. A central authorization engine
`IAuthorizationEngine.Authorize(request) → AuthorizationResult` is the one
decision point (`AuthorizationEngine.cs`). It is **deny-by-default** and every
result carries a human-readable **reason**, so denials are explainable and
old-vs-new decisions can be logged side by side. Evaluation order:

1. Unknown permission ⇒ deny (fail closed).
2. No active role in scope ⇒ deny.
3. No role grants the permission ⇒ deny (reason names the roles + permission).
4. Step-up gates: `RequiresMfa` without MFA, `RequiresFreshAuth` without recent
   re-auth, `RequiresApproval` without a second-party approval ⇒ deny with the
   specific reason.
5. Otherwise allow.

### 3. Behaviour-preserving migration (the safe part)
P2 changes **no one's access**. Each permission is tagged with the
`LegacyCapability` it currently maps onto, and the role→permission matrix
(`RolePermissions`, held as data) is proven **equivalent** to the existing
`Roles.Can*` predicates by `PermissionEngineTests`
(`Role_Matrix_Exactly_Reproduces_Legacy_Capability_Checks` +
`Engine_Decisions_Match_The_Legacy_Matrix_When_Requirements_Are_Met`, over every
role × every permission). This makes the catalog a faithful re-expression of
today's authZ, not a redesign of it.

### 4. Rollout: map → shadow → enforce (done)
1. **Map** — every tenant-scoped endpoint declares its permission (the `Forbidden`
   helper takes a `PermissionDefinition`).
2. **Shadow** — the engine ran alongside the legacy check, logging disagreements
   (zero, by the parity tests).
3. **Enforce** — the engine is now the gate. `Forbidden` returns the engine's
   decision with a status split that preserves anti-enumeration:
   - unauthenticated **or a non-member of the tenant** → `401` (indistinguishable
     from "no such tenant", so cross-tenant probes learn nothing);
   - a **member** whose role lacks the permission, or who needs step-up → `403`
     with the engine's `Reason` (safe — they already know it is their tenant, and
     the reason drives the UI).

**Step-up (`RequiresFreshAuth`).** Sessions carry `LastAuthenticatedAt` +
`MfaSatisfied`. `Authenticate` reports `FreshAuth` (within `AuthService.StepUpWindow`,
10 min) and `MfaSatisfied`, which `Forbidden` passes to the engine. High-risk
permissions (decommission, change-role, remove, deactivate, billing manage) thus
require a recent re-auth; `POST /api/auth/step-up` re-verifies the password (+ an
MFA code when enabled) and refreshes the window. The operator console must handle
a `403` on those actions by prompting step-up — a **frontend follow-up**, so this
branch is not merged to production until that lands.

## Consequences

- **+** One catalog + one deny-by-default engine with reasons — the foundation
  for the tenant-admin app, effective-permissions preview, access reviews, and an
  authZ regression matrix.
- **+** Metadata (risk, step-up, approval) is declared per action, ready to
  enforce without touching each call site.
- **+** Zero behaviour change at introduction, proven by exhaustive parity tests.
- **−** Temporary duplication: the `LegacyCapability` mapping bridges old and new
  until enforcement lands; it is deleted in P3 when per-permission role grants,
  custom roles, and scopes replace it.

## Not in this scaffold (deferred)

- Per-permission role grants, custom roles, delegation limits, and hierarchical
  **scopes** (tenant → site → lab → department) — **P3**.
- **Separation of duty** (author≠approver, requester≠approver) as a rules engine
  — **P3** (§9). The `RequiresApproval` flag reserves the hook.
- The operator-console step-up flow (handle a `403` on a fresh-auth-gated action
  by prompting re-auth against `POST /api/auth/step-up`) — a **frontend
  follow-up** before this branch merges to production.
- `RequiresMfa`-gated permissions — the signal (`MfaSatisfied`) is plumbed, but no
  catalog permission requires MFA yet; set it when an action warrants it.

## Alternatives considered

- **Keep `Roles.Can*` predicates:** rejected — no catalog, no metadata, no reasons,
  no central point; can't build admin/review/step-up on top.
- **External policy engine (OPA/Cedar):** deferred — an in-process, typed catalog
  keeps decisions co-located, greppable, and unit-testable against the existing
  role model; revisit if cross-service policy sharing is needed.
