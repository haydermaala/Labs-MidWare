using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlPlane.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddLifecycleState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Active",
                table: "tenants",
                type: "boolean",
                nullable: false,
                // Backfill: rows that predate this column were active. New rows always
                // set Active explicitly via the entity, so the DB default is only a
                // safety net and 'true' is the correct, safe value for both.
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "Active",
                table: "gateways",
                type: "boolean",
                nullable: false,
                // Backfill: rows that predate this column were active. New rows always
                // set Active explicitly via the entity, so the DB default is only a
                // safety net and 'true' is the correct, safe value for both.
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Active",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "Active",
                table: "gateways");
        }
    }
}
