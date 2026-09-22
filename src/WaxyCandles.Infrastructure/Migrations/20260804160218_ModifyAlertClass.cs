using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WaxyCandles.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ModifyAlertClass : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsEnabled",
                table: "Alerts",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TriggeredAt",
                table: "Alerts",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsEnabled",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "TriggeredAt",
                table: "Alerts");
        }
    }
}
