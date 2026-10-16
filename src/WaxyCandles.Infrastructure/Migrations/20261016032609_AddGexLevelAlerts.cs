using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WaxyCandles.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGexLevelAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Level",
                table: "Alerts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LevelAsOf",
                table: "Alerts",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "LevelCheckedOn",
                table: "Alerts",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LevelDays",
                table: "Alerts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LevelPrice",
                table: "Alerts",
                type: "decimal(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LevelProximityPercent",
                table: "Alerts",
                type: "decimal(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LevelStrikeRange",
                table: "Alerts",
                type: "decimal(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Level",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "LevelAsOf",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "LevelCheckedOn",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "LevelDays",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "LevelPrice",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "LevelProximityPercent",
                table: "Alerts");

            migrationBuilder.DropColumn(
                name: "LevelStrikeRange",
                table: "Alerts");
        }
    }
}
