using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlPlane.Api.Migrations
{
    /// <summary>
    /// Row-Level Security (ADR 0018). ENABLE + FORCE RLS on every tenant-owned
    /// table with a deny-by-default policy keyed on the transaction-local GUC
    /// `app.tenant_id`. `current_setting('app.tenant_id', true)` returns NULL when
    /// unset ⇒ the row is invisible (fail-closed). Two additional device-auth
    /// policies (§6) let a gateway prove possession of a bootstrap token / device
    /// credential to bootstrap authentication before its tenant is known.
    ///
    /// IMPORTANT: FORCE RLS subjects the table owner to policies too, so this must
    /// NOT be deployed until (a) the app connects as the least-privilege
    /// `app_runtime` role and (b) request/device paths set the GUCs per operation.
    /// See ADR 0018 §Rollout. In-memory-provider environments (dev/tests) skip this.
    /// </summary>
    public partial class AddRowLevelSecurity : Migration
    {
        // Tenant-isolation policies (one per tenant-owned table). EF maps entity
        // properties to PascalCase columns (no snake_case convention), so identifiers
        // are double-quoted (unquoted Postgres identifiers fold to lower-case and
        // would miss the real "TenantId"/"Id" columns). device_credentials now carries
        // a denormalized "TenantId" (single-table predicate — no gateway join — which
        // also avoids the policy recursion the device-auth policies would otherwise
        // trigger; see ADR 0018 §6).
        //
        // internal (not private) so the migration-gate test (ADR 0018 §4) can assert
        // every tenant-owned table in the model is covered here.
        internal static readonly (string Table, string Predicate)[] Policies =
        {
            ("gateways", "\"TenantId\" = current_setting('app.tenant_id', true)"),
            ("bootstrap_tokens", "\"TenantId\" = current_setting('app.tenant_id', true)"),
            ("configs", "\"TenantId\" = current_setting('app.tenant_id', true)"),
            ("audit", "\"TenantId\" = current_setting('app.tenant_id', true)"),
            ("memberships", "\"TenantId\" = current_setting('app.tenant_id', true)"),
            ("invitations", "\"TenantId\" = current_setting('app.tenant_id', true)"),
            ("subscriptions", "\"TenantId\" = current_setting('app.tenant_id', true)"),
            ("billing_events", "\"TenantId\" = current_setting('app.tenant_id', true)"),
            ("device_credentials", "\"TenantId\" = current_setting('app.tenant_id', true)"),
            ("tenants", "\"Id\" = current_setting('app.tenant_id', true)"),
        };

        // Auxiliary permissive policies (ADR 0018 §6, §8): added alongside the tenant
        // policies (OR-combined). Each reveals only the row(s) the caller proves a
        // right to via a transaction-local GUC — a presented secret (device
        // enroll/credential, invitation token) or the caller's own user id. All are
        // single-table predicates, so there is no policy recursion.
        // (Name, Table, USING, WITH CHECK|null.)
        private static readonly (string Name, string Table, string Using, string Check)[] AuxiliaryPolicies =
        {
            // Enroll: reveal a bootstrap token only to a caller presenting that token.
            ("bootstrap_tokens_device_auth", "bootstrap_tokens",
                "\"Token\" = current_setting('app.device_token', true)",
                "\"Token\" = current_setting('app.device_token', true)"),
            // Credential auth: reveal a device credential only when BOTH the gateway id
            // and the credential match — a gateway-id-only caller cannot read the secret.
            // Read-only (no WITH CHECK): device_credentials is written under the tenant
            // policy during enrollment, never under device-auth.
            ("device_credentials_device_auth", "device_credentials",
                "\"GatewayId\" = current_setting('app.device_gateway', true) "
                + "AND \"Credential\" = current_setting('app.device_credential', true)",
                null),
            // Invitation accept: reveal an invitation only to a caller presenting its
            // (hashed) token — the tenant is unknown until the token is matched.
            ("invitations_token_auth", "invitations",
                "\"TokenHash\" = current_setting('app.invitation_token_hash', true)",
                "\"TokenHash\" = current_setting('app.invitation_token_hash', true)"),
            // Self read: a user may read their OWN memberships across tenants (drives
            // the tenant switcher). Read-only; memberships are written tenant-scoped.
            ("memberships_self_read", "memberships",
                "\"UserId\" = current_setting('app.user_id', true)",
                null),
        };

        // Platform policy (ADR 0018 §7): a permissive cross-tenant READ of the
        // tenant *registry* only, gated on the transaction-local `app.platform`
        // flag. Trusted server-side operations that legitimately span tenants
        // (the admin tenant list; resolving tenant names across a user's
        // memberships) set the flag; single-tenant lookups do NOT — they stay
        // tenant-scoped. Scoped to `tenants` on purpose: it grants no cross-tenant
        // access to actual tenant DATA (gateways/configs/audit/…). The full
        // super-admin cross-tenant surface is P6 (named platform roles).
        private const string PlatformFlagPredicate = "current_setting('app.platform', true) = 'true'";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // RLS is a PostgreSQL feature; the in-memory provider (dev/tests) has no
            // relational schema to guard. Guard so those environments are unaffected.
            if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                return;
            }

            foreach (var (table, predicate) in Policies)
            {
                migrationBuilder.Sql($"ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql(
                    $"CREATE POLICY {table}_tenant_isolation ON {table} " +
                    $"USING ({predicate}) WITH CHECK ({predicate});");
            }

            foreach (var (name, table, usingExpr, check) in AuxiliaryPolicies)
            {
                var checkClause = check is null ? string.Empty : $" WITH CHECK ({check})";
                migrationBuilder.Sql($"CREATE POLICY {name} ON {table} USING ({usingExpr}){checkClause};");
            }

            // Cross-tenant read of the tenant registry for platform/system operations.
            migrationBuilder.Sql($"CREATE POLICY tenants_platform_read ON tenants USING ({PlatformFlagPredicate});");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                return;
            }

            migrationBuilder.Sql("DROP POLICY IF EXISTS tenants_platform_read ON tenants;");

            foreach (var (name, table, _, _) in AuxiliaryPolicies)
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS {name} ON {table};");
            }

            foreach (var (table, _) in Policies)
            {
                migrationBuilder.Sql($"DROP POLICY IF EXISTS {table}_tenant_isolation ON {table};");
                migrationBuilder.Sql($"ALTER TABLE {table} NO FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE {table} DISABLE ROW LEVEL SECURITY;");
            }
        }
    }
}
