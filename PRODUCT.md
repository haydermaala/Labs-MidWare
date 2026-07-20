# PRODUCT.md — LabConnect

- Status: Source of truth (living document). Started Phase A of the production plan.
- Product: **LabConnect** — laboratory analyzer connectivity middleware.
- Production URL: `https://lc.spottiq.com`
- Controlling specs: `PRODUCTION_EXECUTION_PLAN.md` (launch), `DEVELOPMENT_PLAN Procution.md` (architecture + clinical safety).

## 1. What LabConnect is

A multi-tenant SaaS **control plane** plus an onsite **edge gateway** that connects
laboratory analyzers to LIS/HIS systems through a consistent, validated interface.

```text
Analyzer → Rust gatewayd (Windows/macOS service) → secure outbound channel
         → LabConnect control plane (lc.spottiq.com) → LIS/HIS
```

The browser application manages the fleet; it never replaces the native edge
gateway for serial/TCP/file device connectivity or offline operation.

## 2. Who it serves (personas → baseline roles)

| Persona | Role(s) | Primary jobs |
|---|---|---|
| Lab owner / director | owner, tenant admin | tenant setup, billing, users, sites |
| Lab IT / integrator | lab admin | gateways, devices, connections, LIS/HIS |
| Bench technician | technician | device status, message inspection, diagnostics |
| Mapping reviewer | mapping reviewer | test/unit/terminology mapping review |
| Clinical approver | clinical approver | validation sign-off; release decisions |
| Billing contact | billing admin | subscription, invoices, payment methods |
| Compliance/QA | auditor, read-only | audit trails, lineage, reports |
| LabConnect staff | platform admin, support | tenant health, releases, support cases |

## 3. Product pillars

1. **Safety before convenience** — passive capture by default; nothing is released
   to a patient record without device-specific validation and sign-off.
2. **Provenance everywhere** — every result links raw bytes → parser/driver
   version → mapping version → validation → delivery → acknowledgement.
3. **Fleet clarity** — one screen tells you what is connected, healthy, queued,
   failing, and why.
4. **Tenant isolation as a feature** — server-side checks on every operation;
   audited membership and access.
5. **Operable by design** — offline-tolerant edge, durable queues, runbooks,
   observable everything.

## 4. Scope (production launch)

In scope: authentication (email/password + MFA), tenant/site/user/role/invitation
administration, device inventory + onboarding + connection profiles + message
inspection + mappings + validation + diagnostics, gateway fleet + enrollment +
downloads + updates + certificates, driver registry + signing + certification +
rollback, orders/results operational views + delivery failures + reconciliation,
SMTP configuration + templates + suppressions, plans/subscriptions/billing
(sandbox until live pricing approval), settings, platform administration, public
landing/pricing/security/docs pages, monitoring/backups/rollback, DNS + TLS at
`lc.spottiq.com`.

Out of scope for launch: physical analyzer connection (separate authorization),
clinical result release to real patients, FHIR breadth, custom roles, OIDC/SAML
SSO (designed later as enterprise features), Kubernetes.

## 5. Route map (information architecture)

Public: `/` landing · `/pricing` · `/security` · `/docs` · `/status` (link-out) ·
`/legal/*` (placeholders pending review).

Auth: `/signin` · `/signup` (policy-gated) · `/verify-email` · `/forgot-password` ·
`/reset-password` · `/invite/:token` · `/mfa` (enroll/challenge/recovery) ·
`/sessions`.

App shell (tenant-scoped): `/dashboard` · `/devices` (+`/:id` tabs: overview,
connection, messages, mappings, validation, diagnostics, audit) · `/gateways`
(+`/:id`, `/enroll`, `/downloads`) · `/drivers` (+`/:id`, versions, review) ·
`/messages` (explorer) · `/deliveries` (failures/reconciliation) · `/reports` ·
`/settings/*` (general, sites, users, security, smtp, notifications, api,
webhooks, connectors, retention, billing, audit, developer) · `/billing/*`
(plan, checkout, portal, invoices).

Platform admin (staff only): `/admin/*` (tenants, health, subscriptions, flags,
support, security events, jobs, email delivery, releases, audit).

## 6. Existing foundation (do not rebuild)

The monorepo already implements (built and verified in prior phases): Rust
workspace (canonical model, transports incl. passive-capture guarantee, ASTM +
HL7 engines, driver runtime with ed25519 signing, durable queue, gatewayd,
simulator), control-plane API (ASP.NET Core + EF Core/Postgres, tenants,
enrollment, config, heartbeat/liveness, lifecycle, audit, CORS), fleet console
(React), technician app (Tauri), CI (3 stacks + security scans), Railway deploy
with migrations, 16 ADRs. The production build extends this foundation.

## 7. Non-negotiables (clinical/device safety)

- Synthetic or irreversibly de-identified data only, until separately authorized.
- Unknown analyzers are capture-only; no arbitrary commands to physical devices.
- Parsing success ≠ clinical certification.
- No result release without controlled validation and laboratory sign-off.
- PHI never appears in logs, metrics labels, screenshots, or support bundles.
