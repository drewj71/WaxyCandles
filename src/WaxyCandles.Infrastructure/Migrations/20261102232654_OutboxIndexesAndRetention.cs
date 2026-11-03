using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WaxyCandles.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OutboxIndexesAndRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Type",
                table: "OutboxMessages",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedMessages_ProcessedAt",
                table: "ProcessedMessages",
                column: "ProcessedAt");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Pending",
                table: "OutboxMessages",
                column: "CreatedAt",
                filter: "[ProcessedAt] IS NULL")
                .Annotation("SqlServer:Include", new[] { "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Processed",
                table: "OutboxMessages",
                column: "ProcessedAt",
                filter: "[ProcessedAt] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProcessedMessages_ProcessedAt",
                table: "ProcessedMessages");

            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_Pending",
                table: "OutboxMessages");

            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_Processed",
                table: "OutboxMessages");

            migrationBuilder.AlterColumn<string>(
                name: "Type",
                table: "OutboxMessages",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200);
        }
    }
}
