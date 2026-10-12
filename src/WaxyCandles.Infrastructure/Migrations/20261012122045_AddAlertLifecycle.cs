using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WaxyCandles.Infrastructure.Migrations
{
    /// <summary>
    /// Repeating alerts, cooldown, expiry and a fire count. Existing alerts keep
    /// their behaviour: one-time (Frequency 1 = Once) with the default cooldown.
    /// </summary>
    public partial class AddAlertLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CooldownMinutes",
                table: "Alerts",
                type: "int",
                nullable: false,
                defaultValue: 15);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiresAt",
                table: "Alerts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Frequency",
                table: "Alerts",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TriggerCount",
                table: "Alerts",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Alerts that already fired (each could only fire once before this).
            migrationBuilder.Sql(
                "UPDATE Alerts SET TriggerCount = 1 WHERE IsTriggered = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CooldownMinutes",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "Frequency",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "TriggerCount",
                table: "Alerts");
        }
    }
}
