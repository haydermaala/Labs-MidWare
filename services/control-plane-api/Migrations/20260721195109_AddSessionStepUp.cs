using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlPlane.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionStepUp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastAuthenticatedAt",
                table: "user_sessions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<bool>(
                name: "MfaSatisfied",
                table: "user_sessions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Existing sessions authenticated at CreatedAt; seed LastAuthenticatedAt
            // from it so they are not treated as never-authenticated (a permanent
            // step-up prompt) after deploy. PostgreSQL only.
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql(
                    "UPDATE user_sessions SET \"LastAuthenticatedAt\" = \"CreatedAt\";");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastAuthenticatedAt",
                table: "user_sessions");

            migrationBuilder.DropColumn(
                name: "MfaSatisfied",
                table: "user_sessions");
        }
    }
}
