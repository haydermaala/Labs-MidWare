# Multi-tenancy + RBAC + super-admin — program plan

Grounded gap analysis and phased plan for the master prompt
(`CLAUDE_MULTITENANCY_RBAC_SUPERADMIN_PROMPT.md`). This is a **multi-phase
program**, not one change. LabConnect is **live in production** (`lc.spottiq.com`),
so every phase ships behind a flag, is reversible, and — for RLS — follows the
Section 21 discipline (backup + demonstrated restore, staging first, shadow
logging, canary, rollback) before any production enforcement.

## Current state (what already exists)

| Area | Today |
|---|---|
| Tenancy | Flat `tenants` (id, name, lifecycle status, active). No sites/labs/departments. |
| Membership | `memberships` (user↔tenant, **one** role, active). No scopes, no expiry. |
| Roles | 9 code-owned roles (`Roles.*`) with greppable capability fns (`CanView`/`CanManageFleet`/`CanManageUsers`/`CanManageTenant`/`CanManageBilling`). |
| AuthZ | Per-endpoint `AuthorizedInTenant()` + role capability checks. No central engine, no permission catalog. |
| Identity | PBKDF2, hashed opaque sessions, RFC-6238 MFA + recovery, hashed single-use invitations, last-owner + owner-escalation guards. |
| Billing | Plans/entitlements/subscriptions, server-side gateway quota (402), idempotent signed webhooks. |
| Audit | Append-only `audit` table, tenant-scoped. Not tamper-evident (no hash chain). |
| Isolation | Application-layer: `IControlPlaneStore` filters every query by `tenant_id`. **No Postgres RLS.** Runtime DB role privilege level unverified. |
| Platform admin | A single all-powerful `ControlPlane__AdminToken` bearer. No platform roles, no super-admin app, no support-access grants. |

## Gap analysis vs. the prompt (major, by theme)

1. **RLS + DB-role model** (§5.7) — no `ENABLE/FORCE ROW LEVEL SECURITY`, no default-deny policies, no per-request `SET LOCAL app.tenant_id`, runtime role likely owns tables / may be superuser. *Highest-risk retrofit; also the strongest defense-in-depth.*
2. **Tenancy hierarchy** (§4.1, §5.2) — no sites → laboratories → departments, no `membership_scopes`.
3. **Permission catalog** (§6) — authZ is role-name capability checks, not versioned `<domain>.<resource>.<action>` permissions with metadata (risk_level, delegable, requires_mfa, requires_fresh_auth, requires_approval).
4. **Policy engine** (§8) — no single `authorize(subject, action, resource, context) → allow|deny+reason`. Checks are scattered.
5. **Separation of duty** (§9) — only ad-hoc guards (last-owner, owner-escalation). No static/dynamic SoD engine; mapping author≠approver and command request≠approve not generally enforced.
6. **Roles model** (§5.3, §7) — no custom tenant roles, role inheritance, `role_assignment_scopes`, or expiring assignments.
7. **Access reviews** (§5.3) — none.
8. **Platform / super-admin** (§7.2, §13) — no `platform_roles`/assignments, no `/platform-admin` boundary, no split of the all-powerful admin token into least-privilege platform roles.
9. **Support access** (§13.5) — no time-boxed, approved, audited, read-only-by-default support grants (currently: no support flow at all; the admin token is the blunt instrument).
10. **Lifecycle orchestration** (§10) — no provisioning/suspension/offboarding workflows, jobs, or outbox; deletion/export/retention/legal-hold absent.
11. **Tamper-evident audit** (§16) — plain append table; no hash chain / integrity.
12. **Background processing** (§17) — no durable jobs/outbox; no `admin_action_requests`/approvals.
13. **Step-up auth** — MFA exists but no per-permission `requires_fresh_authentication` gating on high-risk ops.
14. **Isolation/authZ regression + ASVS** (§19) — no cross-tenant isolation matrix, authZ matrix from a catalog, RLS-bypass tests, or ASVS traceability.
15. **Storage/cache/queue scoping** (§4.2) — R2 not wired; no cache/queue to scope yet.

## Phased plan (vertical slices — one focused session each)

Each slice: schema + migration → service/enforcement → tests → verify live on staging → ship behind a flag → doc. Order matches prompt §24.

- **P1 — Tenant context + RLS foundation.** Immutable `TenantContext`; middleware sets `SET LOCAL app.*` GUCs; a **least-privilege** runtime DB role (not owner, not superuser, no `BYPASSRLS`); `ENABLE`+`FORCE RLS` + default-deny `USING`/`WITH CHECK` on tenant tables; a migration-gate test (new tenant table ⇒ must have `tenant_id` + RLS + indexes). **Backup + restore demo first; staging → shadow → canary → prod.**
- **P2 — Permission catalog + policy engine.** `permission_definitions` seeded by migration; `IAuthorizationEngine.Authorize(...)`; map existing capability checks onto permissions; shadow-log old-vs-new decisions; then enforce.
- **P3 — Hierarchy, roles, scopes, invitations upgrade.** Sites/labs/departments (if in scope — see decision D4); `role_assignments` + scopes + expiry; custom roles gated by entitlement with delegation limits; SoD rules table.
- **P4 — Tenant administration app.** Role-aware nav, permission matrix, effective-permissions preview, "why can/can't X", access reviews.
- **P5 — Subscription entitlements deepening.** Extend the existing billing to full quotas/usage/grace/dunning per §12.
- **P6 — Platform super-admin.** `/platform-admin` boundary, platform roles (split the admin token), mandatory MFA/step-up, dashboards, tenant lifecycle actions.
- **P7 — Support access + lifecycle/offboarding.** Support grants; provisioning/suspension/offboarding workflows; jobs/outbox; tamper-evident audit (hash chain).
- **P8 — Regression + security suite.** Isolation matrix, authZ matrix, RLS-bypass, IDOR/BOLA, ASVS traceability.
- **P9 — Production rollout.** Canary, reconciliation, rollback runbook.

## Production-safety notes (this system is LIVE)

- `tenant_id` already exists on tenant-owned tables → the hardest backfill (prompt §21.3) is **already done**. The real P1 risk is switching to a least-privilege runtime role + `FORCE RLS`: if a policy or GUC is wrong, the live app breaks. Mitigate: staging soak → shadow → canary → `restore-drill.sh` proven → prod, with an instant flag/role rollback.
- No destructive one-step migrations. Additive columns/tables, dual-write/shadow where behavior changes, enforce last.

## Locked decisions (2026-07-21)

- **D1 — RLS timing:** **RLS first (P1).** Add RLS + least-privilege runtime DB role as the first slice, before more tables exist. Staging → shadow → canary → prod, with a proven `restore-drill.sh` and instant role/flag rollback.
- **D2 — Program scope:** **Full program (P1–P9).**
- **D3 — Execution model:** **One focused vertical slice per session,** this doc as the persistent anchor. "Refine the plan first" gate: architecture is now locked; building starts at P1.
- **D4 — Hierarchy depth:** **Full hierarchy now** — tenant → sites → laboratories → departments → gateways/devices, with `membership_scopes` at each level. The RLS policies and permission `allowed_scope_types` are designed for this from P1.
- **Super-admin:** **Split `ControlPlane__AdminToken` into named platform roles** (Ops, Support, Billing, Security, Auditor, Release, Root-break-glass) behind a `/platform-admin` boundary with mandatory MFA + step-up + enhanced audit (P6). The legacy admin token is retained only as a bootstrap/break-glass path during migration, then retired.

## P1 concrete scope (next session)

1. **Least-privilege runtime DB role.** New Postgres role `app_runtime` (LOGIN, no superuser, no `BYPASSRLS`, not owner of tenant tables); migrations/backup run as separate roles. Wire `DATABASE_URL` to `app_runtime` in staging first.
2. **Tenant context + GUC middleware.** Immutable `TenantContext` (per §4.3). A per-request DB interceptor issues `SET LOCAL app.user_id/app.tenant_id/app.membership_id/app.support_grant_id` inside the request transaction, from the *verified* session/membership only — never a request-supplied tenant id.
3. **RLS on existing tenant tables** (`gateways`, `device_credentials`, `bootstrap_tokens`, `configs`, `audit`, `memberships`, `invitations`, `subscriptions`, `billing_events`): `ENABLE` + `FORCE ROW LEVEL SECURITY`; default-deny `USING`/`WITH CHECK` keyed on `current_setting('app.tenant_id')`.
4. **Migration-gate test:** a test that fails if any tenant-owned table lacks `tenant_id`, RLS enabled+forced, applicable policies, or a leading `tenant_id` index.
5. **Rollout discipline:** demonstrate `restore-drill.sh` on a prod backup → enable RLS in staging behind a flag → shadow-log any policy denials → canary → prod. Keep the app-layer `IControlPlaneStore` tenant filters (RLS is defense-in-depth, not a replacement).

Exit P1: staging runs entirely under `app_runtime` with `FORCE RLS`; isolation smoke proves Tenant B cannot read Tenant A rows even with app filters bypassed; rollback (swap `DATABASE_URL` back + `NO FORCE`) rehearsed.

## P1 mechanism — PROVEN (validated on postgres:16, 2026-07-21)

The RLS + session-GUC pattern below was verified end-to-end on a throwaway
Postgres (no production touched). Every property held: tenant-A context sees only
A's rows; cross-tenant insert is blocked by `WITH CHECK`; **no context ⇒ 0 rows
(fail-closed)**; `app_runtime` cannot bypass (no superuser/BYPASSRLS). The real
P1 migration builds directly from this.

```sql
-- Least-privilege runtime role — the app connects as this, NOT the owner.
CREATE ROLE app_runtime LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
GRANT SELECT, INSERT, UPDATE, DELETE ON <tenant_table> TO app_runtime;

ALTER TABLE <tenant_table> ENABLE ROW LEVEL SECURITY;
ALTER TABLE <tenant_table> FORCE  ROW LEVEL SECURITY;   -- owner is subject too

-- Single admit-current-tenant policy; deny-by-default by omission.
-- current_setting(..., true) => NULL when unset => row invisible (fail-closed).
CREATE POLICY <t>_tenant_isolation ON <tenant_table>
  USING      (tenant_id = current_setting('app.tenant_id', true))
  WITH CHECK (tenant_id = current_setting('app.tenant_id', true));
```

App side: a per-request DB interceptor runs `SET LOCAL app.tenant_id = '<verified>'`
inside the request transaction, sourced ONLY from the authenticated session's
verified membership (never a request-supplied id). `SET LOCAL` is transaction-scoped,
so pooled connections never leak context across requests.

**Next session (P1 build):** wire this into an EF migration for all tenant tables,
add the Npgsql interceptor from `TenantContext`, the migration-gate test, and the
staging→shadow→canary→prod rollout (restore-drill first; keep app-layer filters).
Do NOT merge the RLS migration to main until the staged prod cutover — Railway's
startup `Database.Migrate()` would apply it to prod on merge.

## Open (resolve during P1/P2, not blocking)

- Named holders of platform Root/break-glass and Clinical Approver roles (§11 OPEN carried from `validation-strategy.md`).
- Regulatory framework applicability (IEC 62304 / ISO 13485 / IVDR) — legal/quality workstream, not a code checkbox.
- Data-region routing abstraction shape for future dedicated-DB enterprise tenants (design seam only in P1; no implementation).
