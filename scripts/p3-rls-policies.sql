-- P3 Row-Level Security policies (pre-staged for the P1→P2→P3 merge).
--
-- The six P3 tables are all plain tenant-owned tables: every row carries a
-- "TenantId" and is only ever read/written with a known authorized-tenant context
-- (the admin the endpoint already authorized in that tenant). So each gets the
-- SAME single-table, deny-by-default isolation policy as the P1 tenant tables —
-- no join-self policy (device_credentials) and no token-auth auxiliary policy
-- (bootstrap_tokens/invitations) are needed here.
--
-- Pattern matches P1's AddRowLevelSecurity exactly (ADR 0018):
--   * columns are quoted PascalCase — EF maps "TenantId" (tables are lowercase);
--   * current_setting('app.tenant_id', true) returns NULL when the GUC is unset,
--     so a query with no tenant context matches no rows (fails closed);
--   * ENABLE + FORCE so even a non-superuser table owner is subject to the policy;
--   * USING gates reads/updates/deletes, WITH CHECK gates inserts/updates.
--
-- At merge this becomes the Up() body of an AddP3RowLevelSecurity EF migration
-- (migrationBuilder.Sql(...)); align the policy NAME with P1's existing convention
-- if it differs. Idempotent (DROP ... IF EXISTS) so it is safe to re-run.

DO $$
DECLARE
    t text;
    tables text[] := ARRAY[
        'scopes',
        'role_assignments',
        'sod_rules',
        'custom_roles',
        'role_permissions',
        'approval_requests'
    ];
BEGIN
    FOREACH t IN ARRAY tables LOOP
        EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY;', t);
        EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY;', t);
        EXECUTE format('DROP POLICY IF EXISTS %I ON %I;', t || '_tenant_isolation', t);
        EXECUTE format(
            'CREATE POLICY %I ON %I '
            'USING ("TenantId" = current_setting(''app.tenant_id'', true)) '
            'WITH CHECK ("TenantId" = current_setting(''app.tenant_id'', true));',
            t || '_tenant_isolation', t);
    END LOOP;
END $$;

-- gateways.ScopeId (added by AddGatewayScopeId): NO new policy. The gateways table
-- already has its P1 tenant-isolation policy; ScopeId is just another column under
-- it. A gateway's ScopeId is validated to belong to the same tenant at assign time
-- (app layer), and a cross-tenant scope id is unreachable anyway because the caller
-- only ever sees its own tenant's scopes.
