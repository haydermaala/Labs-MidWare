using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlPlane.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceCredentialTenantId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                table: "device_credentials",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Backfill the denormalized tenant from each credential's owning gateway
            // so existing rows are correctly scoped before RLS is enabled (ADR 0018).
            // PostgreSQL only; the in-memory provider has no relational schema.
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql(
                    "UPDATE device_credentials AS dc SET \"TenantId\" = g.\"TenantId\" " +
                    "FROM gateways AS g WHERE g.\"Id\" = dc.\"GatewayId\";");
            }

            migrationBuilder.CreateIndex(
                name: "IX_device_credentials_TenantId",
                table: "device_credentials",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_device_credentials_TenantId",
                table: "device_credentials");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "device_credentials");
        }
    }
}
