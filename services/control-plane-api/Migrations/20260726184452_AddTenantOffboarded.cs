using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlPlane.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantOffboarded : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Offboarded",
                table: "tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "platform_offboard_requests",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    SubjectTenantId = table.Column<string>(type: "text", nullable: false),
                    RequesterUserId = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedByUserId = table.Column<string>(type: "text", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_offboard_requests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_platform_offboard_requests_SubjectTenantId",
                table: "platform_offboard_requests",
                column: "SubjectTenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "platform_offboard_requests");

            migrationBuilder.DropColumn(
                name: "Offboarded",
                table: "tenants");
        }
    }
}
