using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WaxyCandles.Infrastructure.Migrations
{
    /// <summary>
    /// One watchlist per user becomes named lists. <c>Watchlists</c> held one
    /// row per (UserId, Symbol); it now holds the lists (Id, UserId, Name,
    /// SortOrder, CreatedAt) and <c>WatchlistItems</c> their symbols.
    /// <para>
    /// Up moves each user's rows into one list named "My Watchlist" (created at
    /// their earliest AddedAt), keeping each row's Id and AddedAt, ordered by
    /// AddedAt (ties by Id). Down keeps one row per (UserId, Symbol) across
    /// all of a user's lists (the earliest added); list names and orders are lost.
    /// </para>
    /// <para>
    /// Hand-written: the scaffolded version altered the table in place and
    /// dropped every symbol. Both directions rename the old table aside (after
    /// dropping its primary key, whose name the new table reuses) with
    /// sp_rename, create the
    /// new shape, copy, then drop the old one, all in the migration's transaction.
    /// </para>
    /// </summary>
    public partial class NamedWatchlists : Migration
    {
        private const string LegacyEntries = "WatchlistEntriesBeforeNamedLists";
        private const string NamedLists = "WatchlistsBeforeRollback";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Watchlists_UserId_Symbol",
                table: "Watchlists");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Watchlists",
                table: "Watchlists");

            // sp_rename rather than RenameTable: EF's SQL Server generator
            // tracks tables by name and fails when one is renamed and a new
            // table of the same name is created in the same migration.
            migrationBuilder.Sql($"EXEC sp_rename N'[dbo].[Watchlists]', N'{LegacyEntries}';");

            migrationBuilder.CreateTable(
                name: "Watchlists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Watchlists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WatchlistItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WatchlistId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchlistItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WatchlistItems_Watchlists_WatchlistId",
                        column: x => x.WatchlistId,
                        principalTable: "Watchlists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // One default list per user that had any row.
            migrationBuilder.Sql($"""
                INSERT INTO [Watchlists] ([Id], [UserId], [Name], [SortOrder], [CreatedAt])
                SELECT NEWID(), [UserId], N'My Watchlist', 0, MIN([AddedAt])
                FROM [{LegacyEntries}]
                GROUP BY [UserId];
                """);

            // Every row into its user's list, in the order it was added. The old
            // unique (UserId, Symbol) index guarantees (WatchlistId, Symbol) is unique.
            migrationBuilder.Sql($"""
                INSERT INTO [WatchlistItems] ([Id], [WatchlistId], [Symbol], [SortOrder], [AddedAt])
                SELECT
                    e.[Id],
                    w.[Id],
                    e.[Symbol],
                    CAST(ROW_NUMBER() OVER (PARTITION BY e.[UserId] ORDER BY e.[AddedAt], e.[Id]) - 1 AS int),
                    e.[AddedAt]
                FROM [{LegacyEntries}] AS e
                INNER JOIN [Watchlists] AS w ON w.[UserId] = e.[UserId];
                """);

            migrationBuilder.DropTable(
                name: LegacyEntries);

            migrationBuilder.CreateIndex(
                name: "IX_Watchlists_UserId_Name",
                table: "Watchlists",
                columns: new[] { "UserId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WatchlistItems_Symbol",
                table: "WatchlistItems",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_WatchlistItems_WatchlistId_Symbol",
                table: "WatchlistItems",
                columns: new[] { "WatchlistId", "Symbol" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WatchlistItems_Watchlists_WatchlistId",
                table: "WatchlistItems");

            migrationBuilder.DropIndex(
                name: "IX_Watchlists_UserId_Name",
                table: "Watchlists");

            migrationBuilder.DropPrimaryKey(
                name: "PK_Watchlists",
                table: "Watchlists");

            migrationBuilder.Sql($"EXEC sp_rename N'[dbo].[Watchlists]', N'{NamedLists}';");

            migrationBuilder.CreateTable(
                name: "Watchlists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Watchlists", x => x.Id);
                });

            // One row per (UserId, Symbol): a symbol in several of a user's
            // lists keeps its earliest-added item (its Id and AddedAt).
            migrationBuilder.Sql($"""
                INSERT INTO [Watchlists] ([Id], [UserId], [Symbol], [AddedAt])
                SELECT x.[Id], x.[UserId], x.[Symbol], x.[AddedAt]
                FROM (
                    SELECT
                        i.[Id],
                        l.[UserId],
                        i.[Symbol],
                        i.[AddedAt],
                        ROW_NUMBER() OVER (
                            PARTITION BY l.[UserId], i.[Symbol]
                            ORDER BY i.[AddedAt], i.[Id]) AS [Rank]
                    FROM [WatchlistItems] AS i
                    INNER JOIN [{NamedLists}] AS l ON l.[Id] = i.[WatchlistId]
                ) AS x
                WHERE x.[Rank] = 1;
                """);

            migrationBuilder.DropTable(
                name: "WatchlistItems");

            migrationBuilder.DropTable(
                name: NamedLists);

            migrationBuilder.CreateIndex(
                name: "IX_Watchlists_UserId_Symbol",
                table: "Watchlists",
                columns: new[] { "UserId", "Symbol" },
                unique: true);
        }
    }
}
