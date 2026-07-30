using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlPlane.Api.Migrations
{
    /// <summary>
    /// Row-Level Security for the P3 tables (ADR 0020; pre-staged in
    /// docs/architecture/p3-rls-premerge.md and validated on postgres:16). Extends
    /// <see cref="AddRowLevelSecurity"/> (P1) to the six scope/role/approval tables.
    /// Each is a plain tenant-owned table (carries "TenantId", always accessed with a
    /// known authorized-tenant context), so each gets the SAME deny-by-default,
    /// single-table isolation policy as the P1 tenant tables — no join-self or
    /// token-auth auxiliary policy is needed here.
    ///
    /// Like the P1 migration: quoted PascalCase columns, current_setting(...,true) so
    /// a missing GUC matches nothing (fail-closed), ENABLE + FORCE so even a
    /// non-superuser owner is subject to the policy. In-memory (dev/tests) skips this.
    /// </summary>
    public partial class AddP3RowLevelSecurity : Migration
    {
        // internal (like AddRowLevelSecurity.Policies) so the migration-gate test
        // (RlsCoverageTests) can assert every tenant-owned P3 table is covered.
        internal static readonly (string Table, string Predicate)[] Policies =
        {
            ("scopes", "\"TenantId\" = current_setting('app.tenant_id', true)"),
            ("role_assignments", "\"TenantId\" = current_setting('app.tenant_id', true)"),
            ("sod_rules", "\"TenantId\" = current_setting('app.tenant_id', true)"),
            ("custom_roles", "\"TenantId\" = current_setting('app.tenant_id', true)"),
            ("role_permissions", "\"TenantId\" = current_setting('app.tenant_id', true)"),
            ("approval_requests", "\"TenantId\" = current_setting('app.tenant_id', true)"),
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
