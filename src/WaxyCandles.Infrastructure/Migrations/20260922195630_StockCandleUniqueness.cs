using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WaxyCandles.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class StockCandleUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Symbol",
                table: "StockCandles",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.CreateIndex(
                name: "IX_StockCandles_Symbol_Interval_Timestamp",
                table: "StockCandles",
                columns: new[] { "Symbol", "Interval", "Timestamp" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StockCandles_Symbol_Interval_Timestamp",
                table: "StockCandles");

            migrationBuilder.AlterColumn<string>(
                name: "Symbol",
                table: "StockCandles",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");
        }
    }
}
