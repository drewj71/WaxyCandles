using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WaxyCandles.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWatchlistUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Watchlists_UserId_Symbol",
                table: "Watchlists");

            migrationBuilder.CreateIndex(
                name: "IX_Watchlists_UserId_Symbol",
                table: "Watchlists",
                columns: new[] { "UserId", "Symbol" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Watchlists_UserId_Symbol",
                table: "Watchlists");

            migrationBuilder.CreateIndex(
                name: "IX_Watchlists_UserId_Symbol",
                table: "Watchlists",
                columns: new[] { "UserId", "Symbol" });
        }
    }
}
