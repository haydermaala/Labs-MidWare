# Production Execution Plan — LabConnect at lc.spottiq.com

Version: 1.0  
Purpose: implementation and production deployment plan for Claude Code  
Production URL: `https://lc.spottiq.com`

## 1. Target outcome

Claude Code will build and deploy a production multi-tenant SaaS control plane for the laboratory analyzer middleware described in `DEVELOPMENT_PLAN Procution.md`. The public application will run at `lc.spottiq.com`, while onsite analyzers continue to connect through a separately installed Windows/macOS edge gateway.

The online application must include authentication, tenant administration, users and invitations, device fleet management, device onboarding, connection profiles, mappings, driver registry, subscriptions, billing, notifications, SMTP configuration, audit logs, security settings, gateway downloads, documentation, and operational dashboards.

The browser application cannot directly replace the native edge gateway for reliable RS-232, USB serial, local TCP listener, file-watcher, or offline operation. Production architecture remains:

```text
Analyzer → Windows/macOS Edge Gateway → secure outbound connection
         → Cloud Control Plane at lc.spottiq.com → LIS/HIS
```

## 2. Execution authority and safety limits

The Claude prompt authorizes creation of the new LabConnect project, production services, and the `lc.spottiq.com` subdomain. It does not authorize deletion or modification of unrelated Hostinger websites, DNS records, email accounts, databases, Railway projects, Cloudflare resources, or GitHub repositories.

Claude must:

- Inspect before changing any external account.
- Record existing DNS state before adding records.
- Use additive changes wherever possible.
- Never expose secrets in source, screenshots, logs, terminal output, or chat.
- Never use real patient data during development or deployment validation.
- Never enable analyzer transmission or clinical result release without device-specific validation.
- Pause for the user only when credentials, legal acceptance, payment onboarding, domain ownership confirmation, or an irreversible/high-risk action requires human participation.

## 3. Decisions Claude must resolve during discovery

Claude should discover existing capabilities and propose defaults rather than blocking immediately. Human confirmation is mandatory for:

- GitHub repository owner and repository name.
- Railway workspace/project and billing plan.
- Final SMTP provider and sender domain/address.
- Payment provider account, supported country, settlement currency, tax handling, and production pricing.
- Cloudflare account availability and whether R2 is enabled.
- Required privacy policy, terms, data-processing agreement, and regulatory jurisdiction.
- Windows code-signing certificate and Apple Developer ID/notarization credentials.
- The first physical analyzer and LIS/HIS validation environment.

Recommended default providers:

- GitHub: private repository.
- Application hosting: Railway.
- Database: managed PostgreSQL on Railway.
- Object storage: Cloudflare R2.
- Transactional email: Resend or Postmark; use Hostinger SMTP only if deliverability and API requirements are adequate.
- Payments: Stripe Billing if available for the business jurisdiction; otherwise Paddle or another merchant-of-record provider.
- Error monitoring: Sentry or an approved equivalent.
- Product analytics: privacy-conscious and disabled for clinical payloads.

## 4. Production architecture

### 4.1 Online services

- `control-plane-web`: server-rendered React/Next.js frontend or the approved React architecture.
- `control-plane-api`: ASP.NET Core API.
- `worker`: background email, webhook, billing, audit export, object lifecycle, and notification jobs.
- PostgreSQL: tenant and operational application data.
- Redis or broker only when durable job requirements justify it.
- R2: signed gateway releases, signed driver packages, approved support bundles, exports, and static artifacts.
- Monitoring: uptime, application errors, traces, queue depth, database health, email delivery, webhook failures, and certificate expiry.

### 4.2 Edge services

- `gatewayd`: Rust background service.
- `gateway-desktop`: Tauri 2 + React technician UI.
- SQLite durable local store.
- Outbound-only mutually authenticated connection to the control plane.
- Signed stable/canary update channels.
- Windows Service and macOS launchd packaging.

### 4.3 Environment separation

- Local: synthetic data, simulator, SQLite, local PostgreSQL where needed.
- Development: isolated Railway environment, development R2 bucket, non-production email/payment modes.
- Staging: production-like isolated database/bucket/secrets, test payment mode, dedicated staging hostname.
- Production: separate database, secrets, storage, domains, signing keys, monitoring, backups, retention, and access policy.

No production secret may be reused in development or staging.

## 5. Product information architecture and pages

### 5.1 Public and authentication pages

- Product landing page with precise analyzer-connectivity positioning.
- Pricing and plan comparison.
- Security and trust page.
- Documentation/help entry.
- Service status link.
- Sign in.
- Create tenant/trial, if business policy enables self-service.
- Email verification.
- Forgot password and reset password.
- Accept invitation.
- MFA enrollment and recovery codes.
- Session/device management.
- Terms, privacy, cookie, acceptable-use, and data-processing pages/placeholders pending legal review.

### 5.2 Global application shell

- Tenant switcher for authorized users.
- Laboratory/site selector.
- Command/search palette.
- Notifications center.
- Help and support entry.
- User/profile menu.
- Responsive sidebar with role-aware navigation.
- Clear environment banner outside production.
- Keyboard navigation, focus management, reduced-motion support, WCAG 2.2 AA.

### 5.3 Dashboard

- Fleet health overview.
- Connected, offline, warning, and unconfigured device counts.
- Results/order message flow volumes without exposing PHI in aggregate labels.
- Queue age and failed deliveries.
- Unresolved mappings.
- Driver/update status.
- Security and subscription notices.
- Activity timeline and actionable next steps.

### 5.4 Tenant, organization, and laboratory administration

- Tenant profile, legal name, support contacts, locale, timezone, and branding.
- Sites/laboratories list and hierarchy.
- Create/edit/archive site through drawers; destructive actions require confirmation.
- Departments and operational settings.
- Tenant feature flags and limits.
- Data-retention and regional settings.
- Tenant deletion/export request workflow with strong safeguards.

### 5.5 Users, roles, and invitations

- User directory with status, role, sites, MFA, last access, and invitation state.
- Invite one or many users via drawer/modal with role and site assignment.
- Resend/revoke invitation.
- Suspend/reactivate user.
- Role-based access control: owner, tenant admin, lab admin, technician, mapping reviewer, clinical approver, billing admin, auditor, read-only/support roles.
- Custom roles later; start with explicit permissions and a permissions matrix.
- Audit every membership, role, and invitation change.

### 5.6 Device fleet pages

- Device inventory with search, filters, saved views, health, gateway, driver, site, model, firmware, last communication, and validation status.
- Device detail with overview, connection, messages, mappings, validation, maintenance, audit, and diagnostics tabs.
- Add-device workflow as a progressive drawer/wizard.
- Edit connection in a drawer, not a full navigation context switch.
- Command/control actions separated from passive actions and disabled until authorized.
- Bulk assignment/update only for safe configuration fields.
- Archive rather than delete; preserve clinical/audit lineage.

### 5.7 Device connection pages

- Discover gateway and available serial ports/network endpoints.
- Choose transport: serial, TCP client/server, MLLP, file watcher.
- Configure parameters with validated inputs and safe presets.
- Passive capture mode by default.
- Live byte/frame viewer with PHI redaction controls.
- Protocol detection results with confidence and evidence.
- Connection test, replay, simulator, and troubleshooting.
- Status lifecycle: discovered, capture-only, configured, validating, approved, production, suspended.
- No arbitrary command entry field for production analyzers.

### 5.8 Gateways

- Gateway fleet page with OS, version, site, enrollment state, last seen, certificate expiry, disk/queue health, and update channel.
- “Add gateway” page: generate short-lived enrollment token, installation instructions, checksum/signature verification, and expiry.
- Gateway detail: devices, queue, diagnostics, certificates, releases, audit, configuration versions.
- Gateway downloads page with Windows and macOS artifacts, version notes, hashes, signatures, supported OS matrix, installation and upgrade guides.
- Remote actions limited to safe configuration/update operations with approval and audit.

### 5.9 Drivers and mappings

- Driver registry with certification status, vendor/model/firmware coverage, transports, workflows, version, signature, limitations, and update channel.
- Driver detail/version comparison and rollback.
- Draft profile editor and review workflow.
- Test mappings by device/site with code, name, specimen, unit/UCUM, LOINC suggestion, precision, status, reviewer, and effective date.
- Mapping drawer for fast review; batch confirmation with explicit evidence.
- Validation plans, cases, expected/actual results, discrepancies, approvals, and signed reports.
- AI suggestions clearly labeled and never activated automatically.

### 5.10 Messages, orders, results, and reconciliation

- Message explorer with date/device/status/type filters and secure access controls.
- Raw, framed, parsed, canonical, mapping, delivery, and acknowledgement views.
- Orders and results operational views with identifiers minimized/redacted by role.
- Failed delivery/dead-letter queue with retry, resolve, and export actions.
- Duplicate/correction/reconciliation history.
- No general “edit clinical result” action in the middleware UI.

### 5.11 Subscription and billing

- Plans page and usage/limit presentation.
- Subscription overview, current plan, renewal, status, and included allowances.
- Upgrade/downgrade flow with proration explanation.
- Checkout and customer billing portal integration.
- Payment methods, invoices, receipts, billing contacts, tax/company details.
- Dunning/past-due, grace period, failed payment, cancellation, reactivation, and entitlement states.
- Provider webhooks with signature verification, replay protection, idempotency, and audit.
- Server-authoritative entitlements; never trust frontend plan state.
- Suggested dimensions: base platform, active gateways, active devices, sites, retention, support tier, and optional advanced driver/validation features.
- Pricing and currency remain configurable until business approval.

### 5.12 Settings

- General tenant settings.
- Laboratories/sites.
- Users and access.
- Authentication/MFA/session policies.
- SMTP and email branding.
- Notifications and escalation rules.
- API clients, webhooks, and IP restrictions.
- LIS/HIS connectors.
- Data retention and exports.
- Security, certificates, and support access.
- Billing and subscription.
- Audit log.
- Developer/integration settings.

The settings experience must use a clear left sub-navigation, route-based sections, unsaved-change protection, field-level validation, test actions, masked secrets, rotation/revocation controls, and audit history.

### 5.13 SMTP and email

- Provider adapter interface supporting API-based providers and standard SMTP.
- Configuration fields stored encrypted and never returned to the browser after save.
- Sender domain/address verification status.
- “Send test email” action with throttling and audit.
- Templates for verification, password reset, invitation, MFA/security alerts, billing events, gateway offline, certificate expiry, mapping approval, and support workflows.
- Bounce/complaint/suppression handling.
- Per-tenant branding only where sender-domain policy permits.
- Plain-text alternative, accessible markup, locale readiness, and secure single-use expiring links.

### 5.14 Platform administration

- Internal tenants page, health, subscriptions, feature flags, support cases, security events, job failures, email delivery, release channels, and audit.
- Impersonation discouraged; if unavoidable, require reason, approval policy, visible banner, short expiry, and complete audit.
- No cross-tenant clinical data browsing by default.

## 6. World-class UX/UI direction

Claude must invoke `/ui-ux-pro-max` exactly as requested and follow its full workflow before implementation. It should establish `PRODUCT.md`, `DESIGN.md`, tokens, navigation, wireflows, component states, and responsive specifications.

Design character: precise, calm, operationally confident, and suitable for laboratories. Avoid generic AI SaaS styling, decorative glass, excessive gradients, oversized rounded cards, and dashboard clutter.

Required UX standards:

- Familiar product patterns with strong hierarchy and restrained color.
- Dense information where it supports technicians, with progressive disclosure elsewhere.
- Drawers for create/edit contextual work; dialogs only for focused confirmations or short tasks.
- Tables with saved views, filters, column controls, pagination/virtualization, responsive alternatives, and accessible keyboard operation.
- Complete loading, skeleton, empty, error, permission-denied, offline, stale, and partial-success states.
- Every interactive control: default, hover, focus, active, disabled, loading, success, and error where applicable.
- WCAG 2.2 AA, strong visible focus, 44px touch targets where appropriate, screen-reader semantics, reduced motion, color-independent status.
- Desktop-first for operational screens, fully usable tablet/mobile administration, and no assumption that device setup occurs in a mobile browser.
- English first, architecture ready for Arabic RTL and localization.
- Design tokens in OKLCH or an equivalent controlled system; verified contrast.
- One consistent icon family and no hand-drawn production icons.

Claude should produce and review screenshots for desktop, tablet, and mobile, run accessibility tests, and visually inspect every critical route before production.

## 7. Authentication and authorization

- Email/password authentication with modern password hashing.
- Verified email before sensitive access.
- Secure password reset using single-use, short-lived, hashed tokens.
- Email invitations with single-use expiry, acceptance, and existing-user handling.
- MFA/TOTP and recovery codes; require MFA for privileged roles when configured.
- Secure, HttpOnly, SameSite cookies; CSRF protection; session rotation and revocation.
- Rate limiting, credential-stuffing defenses, audit, and generic authentication errors.
- Tenant membership checked server-side for every request and object lookup.
- Authorization policies tested against cross-tenant access and IDOR.
- OIDC/SAML can be designed as later enterprise features unless already required.

## 8. Data and API design

Core production tables include tenants, sites, memberships, invitations, roles/permissions, sessions, gateways, gateway credentials, devices, connections, drivers/versions, mappings/versions, validations/approvals, raw-message metadata, deliveries, subscriptions, entitlements, invoices/payment references, email configurations, notifications, API clients, webhooks, audit events, and release artifacts.

Use migrations, optimistic concurrency where appropriate, soft archive for lineage, UTC storage, tenant-scoped indexes, explicit retention jobs, and encrypted sensitive configuration. Raw clinical payload storage must be minimized and governed separately.

Expose versioned APIs with OpenAPI, request validation, idempotency for mutation/webhook endpoints, consistent errors, correlation IDs, pagination, rate limits, and audit hooks.

## 9. Delivery phases

### Phase A — Discovery and production design

Inspect accounts and existing resources read-only; establish providers, architecture decisions, threat model, data policy, product/design system through `/ui-ux-pro-max`, backlog, acceptance tests, and rollout plan.

Exit: approved provider/resource map, UX flows, schema/API plan, and no unresolved blocker to scaffolding.

### Phase B — Repository, environments, and foundation

Create private GitHub repository, branch protection, monorepo, CI, development/staging/production environment definitions, secret model, database migrations, logging/monitoring foundation, and local verification.

Exit: green builds/tests and deployment plan; no public traffic yet.

### Phase C — Identity, tenancy, invitations, and settings

Implement authentication, reset, verification, MFA, tenant/site membership, RBAC, invitations, audit, profile, tenant settings, and SMTP adapter/test flow.

Exit: cross-tenant tests pass; email flows verified in non-production mode.

### Phase D — World-class application shell and core pages

Implement the approved design system, responsive shell, dashboard, tenants, sites, users, devices, gateways, drivers, mappings, messages, validation, notifications, settings, support, and platform admin surfaces with realistic synthetic data.

Exit: critical routes pass visual, responsive, accessibility, interaction, and authorization review.

### Phase E — Billing and subscriptions

Implement product/price configuration, checkout, portal, subscription lifecycle, invoices, entitlements, webhooks, dunning states, billing admin, and tests using provider sandbox.

Exit: webhook replay/idempotency tests and complete sandbox lifecycle pass; user approves live prices before production activation.

### Phase F — Edge gateway vertical slice

Implement Rust service/Tauri shell, gateway enrollment, signed releases, simulator, passive transport foundation, durable queue, and cloud fleet status. Produce Windows/macOS development installers.

Exit: simulator-to-cloud flow passes with synthetic data; no physical analyzer writes.

### Phase G — Production infrastructure

Provision Railway production services/database, R2 buckets/tokens, monitoring, backups, restore procedure, production secrets, and release pipelines. Deploy through controlled migrations and health checks.

Exit: production environment passes smoke, security, restore, and rollback tests on temporary/assigned hostnames.

### Phase H — Domain and launch

Inspect Hostinger DNS, add only required records for `lc.spottiq.com`, wait for DNS/TLS, configure canonical URL/cookies/CORS/email links, deploy production, run smoke/E2E/accessibility/security checks, and monitor.

Exit: `https://lc.spottiq.com` healthy with valid TLS, authentication/email/payment flows verified, monitoring and rollback ready.

### Phase I — Physical device pilot

After separate authorization, connect one analyzer in passive/shadow mode, validate against expected results, obtain laboratory approval, then stage unidirectional and later bidirectional workflows.

Exit: signed device-specific validation; no broader compatibility claim.

## 10. DNS and Hostinger procedure

1. Open the existing Hostinger hPanel session for `spottiq.com`.
2. Capture/read current DNS records and confirm no existing `lc` conflict.
3. Determine the production target supplied by Railway or the chosen frontend host.
4. Add the minimum required record, normally CNAME `lc` to the provider target; follow provider verification requirements.
5. Do not change apex, `www`, mail/MX, SPF, DKIM, DMARC, or unrelated records.
6. Verify propagation with authoritative DNS lookup and HTTPS from outside the provider.
7. Confirm TLS, redirect behavior, HSTS readiness, canonical URL, secure cookies, CORS, callback and email-link URLs.
8. Record the before/after state and rollback instructions.

## 11. Production readiness checklist

- All critical/high security issues resolved or approved.
- Tenant isolation and RBAC tests pass.
- Password reset/invitation/MFA/session flows pass.
- SMTP sender verification, SPF/DKIM/DMARC, bounce and suppression behavior verified.
- Payment provider production onboarding and live pricing approved.
- Webhook signatures, idempotency, replay handling, and reconciliation pass.
- Database backup and restore demonstrated.
- R2 lifecycle, access, CORS, and secret scope verified.
- CI/CD approvals, migrations, health checks, canary, and rollback tested.
- TLS, DNS, security headers, CSP, cookie attributes, rate limits, and error redaction verified.
- Accessibility and responsive tests pass for critical routes.
- Monitoring, alerts, status communication, runbooks, support contacts, and incident response active.
- Terms/privacy/cookie/billing language reviewed by qualified advisors.
- No real analyzer result release until separate clinical validation.

## 12. Final production deliverables expected from Claude

- Production repository with documentation and reproducible builds.
- Architecture, product, design system, threat model, data model, API, and ADR documentation.
- Complete tenant-aware web application and administrative surfaces.
- Authentication, invitations, SMTP, subscription, payment, and billing flows.
- Rust/Tauri edge gateway foundation and signed-download workflow.
- Simulator and automated conformance/security/E2E test suites.
- Railway/R2/Hostinger environment documentation and infrastructure definitions.
- Live `https://lc.spottiq.com` deployment.
- DNS before/after record, deployment evidence, test results, backup/restore and rollback evidence.
- Credential inventory by secret name/location without exposing values.
- Known limitations, risk register, operations runbooks, and next device-validation plan.
