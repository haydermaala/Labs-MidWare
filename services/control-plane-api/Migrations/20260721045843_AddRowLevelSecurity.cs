using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlPlane.Api.Migrations
{
    /// <summary>
    /// Row-Level Security (ADR 0018). ENABLE + FORCE RLS on every tenant-owned
    /// table with a deny-by-default policy keyed on the transaction-local GUC
    /// `app.tenant_id`. `current_setting('app.tenant_id', true)` returns NULL when
    /// unset ⇒ the row is invisible (fail-closed).
    ///
    /// IMPORTANT: FORCE RLS subjects the table owner to policies too, so this must
    /// NOT be deployed until (a) the app connects as the least-privilege
    /// `app_runtime` role and (b) request middleware sets `app.tenant_id` per
    /// operation. Until then, an app with no GUC set would see zero rows. See
    /// ADR 0018 §Rollout. In-memory-provider environments (dev/tests) skip this.
    /// </summary>
    public partial class AddRowLevelSecurity : Migration
    {
        // (table, tenant predicate) — device_credentials has no tenant_id, so it
        // authorises through its gateway; tenants authorises on its own id.
        // EF maps entity properties to PascalCase columns (no snake_case convention),
        // so identifiers are double-quoted (unquoted Postgres identifiers fold to
        // lower-case and would miss the real "TenantId"/"Id"/"GatewayId" columns).
        private static readonly (string Table, string Predicate)[] Policies =
        {
            ("gateways", "\"TenantId\" = current_setting('app.tenant_id', true)"),
            ("bootstrap_tokens", "\"TenantId\" = current_setting('app.tenant_id', true)"),
            ("configs", "\"TenantId\" = current_setting('app.tenant_id', true)"),
            ("audit", "\"TenantId\" = current_setting('app.tenant_id', true)"),
            ("memberships", "\"TenantId\" = current_setting('app.tenant_id', true)"),
            ("invitations", "\"TenantId\" = current_setting('app.tenant_id', true)"),
            ("subscriptions", "\"TenantId\" = current_setting('app.tenant_id', true)"),
            ("billing_events", "\"TenantId\" = current_setting('app.tenant_id', true)"),
            ("device_credentials",
                "\"GatewayId\" IN (SELECT \"Id\" FROM gateways WHERE \"TenantId\" = current_setting('app.tenant_id', true))"),
            ("tenants", "\"Id\" = current_setting('app.tenant_id', true)"),
        };

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                return;
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
