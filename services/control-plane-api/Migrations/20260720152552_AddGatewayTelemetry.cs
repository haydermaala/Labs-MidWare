using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ControlPlane.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddGatewayTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CapturedCount",
                table: "gateways",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "DeadCount",
                table: "gateways",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "DeliveredCount",
                table: "gateways",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastCaptureAt",
                table: "gateways",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PendingCount",
                table: "gateways",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CapturedCount",
                table: "gateways");

            migrationBuilder.DropColumn(
                name: "DeadCount",
                table: "gateways");

            migrationBuilder.DropColumn(
                name: "DeliveredCount",
                table: "gateways");

            migrationBuilder.DropColumn(
                name: "LastCaptureAt",
                table: "gateways");

            migrationBuilder.DropColumn(
                name: "PendingCount",
                table: "gateways");
        }
    }
}
