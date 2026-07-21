using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlPlane.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSodRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sod_rules",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    PermissionA = table.Column<string>(type: "text", nullable: false),
                    PermissionB = table.Column<string>(type: "text", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sod_rules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_sod_rules_TenantId",
                table: "sod_rules",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sod_rules");
        }
    }
}
