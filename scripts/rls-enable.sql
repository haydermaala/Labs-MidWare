-- RE-ARM row-level security after an emergency un-gate.
--
-- WHY THIS EXISTS: the rollback in docs/operations/rls-rollout.md relaxes enforcement
-- in place (`NO FORCE` / `DISABLE ROW LEVEL SECURITY`). Nothing puts it back.
-- `Database.Migrate()` will NOT: both RLS migrations are already recorded in
-- `__EFMigrationsHistory`, so their Up() never runs again, and every subsequent deploy
-- silently ratifies the un-gated state. Tenant isolation would stay off forever, with
-- no error and no failing test (RlsCoverageTests is model-level and never touches a
-- live database).
--
-- Run as the OWNER, then verify the count at the bottom returns 16.
-- Idempotent: safe to run whether the tables are currently forced, un-forced, or
-- disabled, and whether the policies exist or not.
\set ON_ERROR_STOP on

BEGIN;

-- ── P1 tenant tables (AddRowLevelSecurity) ────────────────────────────────────
ALTER TABLE gateways           ENABLE ROW LEVEL SECURITY;
ALTER TABLE bootstrap_tokens   ENABLE ROW LEVEL SECURITY;
ALTER TABLE configs            ENABLE ROW LEVEL SECURITY;
ALTER TABLE audit              ENABLE ROW LEVEL SECURITY;
ALTER TABLE memberships        ENABLE ROW LEVEL SECURITY;
ALTER TABLE invitations        ENABLE ROW LEVEL SECURITY;
ALTER TABLE subscriptions      ENABLE ROW LEVEL SECURITY;
ALTER TABLE billing_events     ENABLE ROW LEVEL SECURITY;
ALTER TABLE device_credentials ENABLE ROW LEVEL SECURITY;
ALTER TABLE tenants            ENABLE ROW LEVEL SECURITY;

ALTER TABLE gateways           FORCE ROW LEVEL SECURITY;
ALTER TABLE bootstrap_tokens   FORCE ROW LEVEL SECURITY;
ALTER TABLE configs            FORCE ROW LEVEL SECURITY;
ALTER TABLE audit              FORCE ROW LEVEL SECURITY;
ALTER TABLE memberships        FORCE ROW LEVEL SECURITY;
ALTER TABLE invitations        FORCE ROW LEVEL SECURITY;
ALTER TABLE subscriptions      FORCE ROW LEVEL SECURITY;
ALTER TABLE billing_events     FORCE ROW LEVEL SECURITY;
ALTER TABLE device_credentials FORCE ROW LEVEL SECURITY;
ALTER TABLE tenants            FORCE ROW LEVEL SECURITY;

-- ── P3 tenant tables (AddP3RowLevelSecurity) ──────────────────────────────────
ALTER TABLE scopes            ENABLE ROW LEVEL SECURITY;
ALTER TABLE role_assignments  ENABLE ROW LEVEL SECURITY;
ALTER TABLE sod_rules         ENABLE ROW LEVEL SECURITY;
ALTER TABLE custom_roles      ENABLE ROW LEVEL SECURITY;
ALTER TABLE role_permissions  ENABLE ROW LEVEL SECURITY;
ALTER TABLE approval_requests ENABLE ROW LEVEL SECURITY;

ALTER TABLE scopes            FORCE ROW LEVEL SECURITY;
ALTER TABLE role_assignments  FORCE ROW LEVEL SECURITY;
ALTER TABLE sod_rules         FORCE ROW LEVEL SECURITY;
ALTER TABLE custom_roles      FORCE ROW LEVEL SECURITY;
ALTER TABLE role_permissions  FORCE ROW LEVEL SECURITY;
ALTER TABLE approval_requests FORCE ROW LEVEL SECURITY;

-- ── Tenant-isolation policies (recreated idempotently) ────────────────────────
DROP POLICY IF EXISTS gateways_tenant_isolation ON gateways;
CREATE POLICY gateways_tenant_isolation ON gateways
  USING ("TenantId" = current_setting('app.tenant_id', true))
  WITH CHECK ("TenantId" = current_setting('app.tenant_id', true));
DROP POLICY IF EXISTS bootstrap_tokens_tenant_isolation ON bootstrap_tokens;
CREATE POLICY bootstrap_tokens_tenant_isolation ON bootstrap_tokens
  USING ("TenantId" = current_setting('app.tenant_id', true))
  WITH CHECK ("TenantId" = current_setting('app.tenant_id', true));
DROP POLICY IF EXISTS configs_tenant_isolation ON configs;
CREATE POLICY configs_tenant_isolation ON configs
  USING ("TenantId" = current_setting('app.tenant_id', true))
  WITH CHECK ("TenantId" = current_setting('app.tenant_id', true));
DROP POLICY IF EXISTS audit_tenant_isolation ON audit;
CREATE POLICY audit_tenant_isolation ON audit
  USING ("TenantId" = current_setting('app.tenant_id', true))
  WITH CHECK ("TenantId" = current_setting('app.tenant_id', true));
DROP POLICY IF EXISTS memberships_tenant_isolation ON memberships;
CREATE POLICY memberships_tenant_isolation ON memberships
  USING ("TenantId" = current_setting('app.tenant_id', true))
  WITH CHECK ("TenantId" = current_setting('app.tenant_id', true));
DROP POLICY IF EXISTS invitations_tenant_isolation ON invitations;
CREATE POLICY invitations_tenant_isolation ON invitations
  USING ("TenantId" = current_setting('app.tenant_id', true))
  WITH CHECK ("TenantId" = current_setting('app.tenant_id', true));
DROP POLICY IF EXISTS subscriptions_tenant_isolation ON subscriptions;
CREATE POLICY subscriptions_tenant_isolation ON subscriptions
  USING ("TenantId" = current_setting('app.tenant_id', true))
  WITH CHECK ("TenantId" = current_setting('app.tenant_id', true));
DROP POLICY IF EXISTS billing_events_tenant_isolation ON billing_events;
CREATE POLICY billing_events_tenant_isolation ON billing_events
  USING ("TenantId" = current_setting('app.tenant_id', true))
  WITH CHECK ("TenantId" = current_setting('app.tenant_id', true));
DROP POLICY IF EXISTS device_credentials_tenant_isolation ON device_credentials;
CREATE POLICY device_credentials_tenant_isolation ON device_credentials
  USING ("TenantId" = current_setting('app.tenant_id', true))
  WITH CHECK ("TenantId" = current_setting('app.tenant_id', true));
DROP POLICY IF EXISTS tenants_tenant_isolation ON tenants;
CREATE POLICY tenants_tenant_isolation ON tenants
  USING ("Id" = current_setting('app.tenant_id', true))
  WITH CHECK ("Id" = current_setting('app.tenant_id', true));

DROP POLICY IF EXISTS scopes_tenant_isolation ON scopes;
CREATE POLICY scopes_tenant_isolation ON scopes
  USING ("TenantId" = current_setting('app.tenant_id', true))
  WITH CHECK ("TenantId" = current_setting('app.tenant_id', true));
DROP POLICY IF EXISTS role_assignments_tenant_isolation ON role_assignments;
CREATE POLICY role_assignments_tenant_isolation ON role_assignments
  USING ("TenantId" = current_setting('app.tenant_id', true))
  WITH CHECK ("TenantId" = current_setting('app.tenant_id', true));
DROP POLICY IF EXISTS sod_rules_tenant_isolation ON sod_rules;
CREATE POLICY sod_rules_tenant_isolation ON sod_rules
  USING ("TenantId" = current_setting('app.tenant_id', true))
  WITH CHECK ("TenantId" = current_setting('app.tenant_id', true));
DROP POLICY IF EXISTS custom_roles_tenant_isolation ON custom_roles;
CREATE POLICY custom_roles_tenant_isolation ON custom_roles
  USING ("TenantId" = current_setting('app.tenant_id', true))
  WITH CHECK ("TenantId" = current_setting('app.tenant_id', true));
DROP POLICY IF EXISTS role_permissions_tenant_isolation ON role_permissions;
CREATE POLICY role_permissions_tenant_isolation ON role_permissions
  USING ("TenantId" = current_setting('app.tenant_id', true))
  WITH CHECK ("TenantId" = current_setting('app.tenant_id', true));
DROP POLICY IF EXISTS approval_requests_tenant_isolation ON approval_requests;
CREATE POLICY approval_requests_tenant_isolation ON approval_requests
  USING ("TenantId" = current_setting('app.tenant_id', true))
  WITH CHECK ("TenantId" = current_setting('app.tenant_id', true));

-- ── Auxiliary policies (ADR 0018 §6/§8, as tightened by TightenAuxiliaryRlsPolicies) ──
DROP POLICY IF EXISTS bootstrap_tokens_device_auth ON bootstrap_tokens;
CREATE POLICY bootstrap_tokens_device_auth ON bootstrap_tokens
  USING ("Token" = current_setting('app.device_token', true))
  WITH CHECK ("Token" = current_setting('app.device_token', true));
DROP POLICY IF EXISTS device_credentials_device_auth ON device_credentials;
CREATE POLICY device_credentials_device_auth ON device_credentials FOR SELECT
  USING ("GatewayId" = current_setting('app.device_gateway', true)
     AND "Credential" = current_setting('app.device_credential', true));
DROP POLICY IF EXISTS invitations_token_auth ON invitations;
CREATE POLICY invitations_token_auth ON invitations
  USING ("TokenHash" = current_setting('app.invitation_token_hash', true))
  WITH CHECK ("TokenHash" = current_setting('app.invitation_token_hash', true));
DROP POLICY IF EXISTS memberships_self_read ON memberships;
CREATE POLICY memberships_self_read ON memberships FOR SELECT
  USING ("UserId" = current_setting('app.user_id', true));

-- ── Platform cross-tenant registry read (ADR 0018 §7) ─────────────────────────
DROP POLICY IF EXISTS tenants_platform_read ON tenants;
CREATE POLICY tenants_platform_read ON tenants
  USING (current_setting('app.platform', true) = 'true');

COMMIT;

\echo '--- verification: both numbers must match the expected values ---'
SELECT
  (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public' AND c.relforcerowsecurity) AS forced_tables_expect_16,
  (SELECT count(*) FROM pg_policies WHERE schemaname = 'public') AS policies_expect_21;
