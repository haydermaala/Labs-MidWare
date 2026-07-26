using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlPlane.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "tenants",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Active");

            // Backfill the lifecycle status from the legacy booleans (TenantLifecycle.FromLegacy):
            // Offboarded was terminal → Archived; else Suspended when inactive. The default
            // above already covers the active case. Columns are PascalCase, so quote them.
            //
            // `tenants` has FORCE ROW LEVEL SECURITY (AddRowLevelSecurity), which subjects even
            // the migration/owner role to policies on a managed Postgres — a bulk cross-tenant
            // UPDATE would match 0 rows without app.tenant_id. Drop FORCE just for the backfill
            // (owner-only DDL), run it, then restore FORCE. Atomic within the migration tx.
            migrationBuilder.Sql("ALTER TABLE tenants NO FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(
                """
                UPDATE tenants
                SET "Status" = CASE
                    WHEN "Offboarded" THEN 'Archived'
                    WHEN NOT "Active" THEN 'Suspended'
                    ELSE 'Active'
                END;
                """);
            migrationBuilder.Sql("ALTER TABLE tenants FORCE ROW LEVEL SECURITY;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Status",
                table: "tenants");
        }
    }
}
