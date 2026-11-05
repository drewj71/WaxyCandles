using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WaxyCandles.Infrastructure.Watchlists;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Migrations;

/// <summary>
/// Runs the real NamedWatchlists migration: each user's single watchlist
/// becomes one list named "My Watchlist", keeping every symbol, its Id and
/// AddedAt, in the order the symbols were added; rolling back restores one
/// row per (user, symbol).
/// </summary>
[Collection(IntegrationCollection.Name)]
public class NamedWatchlistsTests(IntegrationFixture fixture)
{
    private const string MigrationBefore = "20261104000740_RefreshTokenFamilies";

    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Each_users_rows_move_into_one_default_list_in_added_order()
    {
        var connectionString = await fixture.CreateDatabaseAsync(MigrationBefore);

        // Inserted out of order; the list order must follow AddedAt.
        var aliceMsft = await SeedLegacyRowAsync(connectionString, "alice", "MSFT", T0.AddMinutes(2));
        var aliceAapl = await SeedLegacyRowAsync(connectionString, "alice", "AAPL", T0);
        var aliceNvda = await SeedLegacyRowAsync(connectionString, "alice", "NVDA", T0.AddMinutes(1));
        var bobAapl = await SeedLegacyRowAsync(connectionString, "bob", "AAPL", T0.AddDays(1));

        await using var db = IntegrationFixture.CreateDbContext(connectionString);
        await db.GetService<IMigrator>().MigrateAsync();

        var lists = await db.Watchlists.Include(x => x.Items).OrderBy(x => x.UserId).ToListAsync();

        Assert.Equal(["alice", "bob"], lists.Select(x => x.UserId));
        Assert.All(lists, x => Assert.Equal("My Watchlist", x.Name));
        Assert.All(lists, x => Assert.Equal(0, x.SortOrder));

        var alice = lists[0];
        Assert.Equal(T0, alice.CreatedAt);

        var aliceItems = alice.Items.OrderBy(x => x.SortOrder).ToList();
        Assert.Equal(["AAPL", "NVDA", "MSFT"], aliceItems.Select(x => x.Symbol));
        Assert.Equal([0, 1, 2], aliceItems.Select(x => x.SortOrder));
        Assert.Equal([aliceAapl, aliceNvda, aliceMsft], aliceItems.Select(x => x.Id));
        Assert.Equal([T0, T0.AddMinutes(1), T0.AddMinutes(2)], aliceItems.Select(x => x.AddedAt));

        var bobItem = Assert.Single(lists[1].Items);
        Assert.Equal(bobAapl, bobItem.Id);
        Assert.Equal("AAPL", bobItem.Symbol);

        // The app sees the same watchlist as before through the old endpoints.
        var service = new WatchlistService(db);
        Assert.Equal(["AAPL", "NVDA", "MSFT"], await service.GetDefaultWatchlistSymbolsAsync("alice"));
        Assert.Equal(["AAPL"], await service.GetDefaultWatchlistSymbolsAsync("bob"));
    }

    [Fact]
    public async Task Migrating_an_empty_table_creates_no_lists()
    {
        var connectionString = await fixture.CreateDatabaseAsync(MigrationBefore);

        await using var db = IntegrationFixture.CreateDbContext(connectionString);
        await db.GetService<IMigrator>().MigrateAsync();

        Assert.Equal(0, await db.Watchlists.CountAsync());
        Assert.Equal(0, await db.WatchlistItems.CountAsync());
    }

    [Fact]
    public async Task Rolling_back_keeps_one_row_per_user_and_symbol()
    {
        var connectionString = await fixture.CreateDatabaseAsync();

        // A symbol in two of alice's lists, plus one in each.
        var techId = await WatchlistSeed.AddAsync(connectionString, "alice", "AAPL", "Tech");
        await WatchlistSeed.AddAsync(connectionString, "alice", "MSFT", "Tech");
        await WatchlistSeed.AddAsync(connectionString, "alice", "AAPL", "Other");
        await WatchlistSeed.AddAsync(connectionString, "alice", "KO", "Other");
        await WatchlistSeed.AddAsync(connectionString, "bob", "AAPL", "Bob's");

        Guid earliestAaplId;
        await using (var db = IntegrationFixture.CreateDbContext(connectionString))
        {
            earliestAaplId = await db.WatchlistItems
                .Where(x => x.WatchlistId == techId && x.Symbol == "AAPL")
                .Select(x => x.Id)
                .SingleAsync();

            // Make the Tech AAPL clearly the earliest added.
            await db.WatchlistItems
                .Where(x => x.Id == earliestAaplId)
                .ExecuteUpdateAsync(x => x.SetProperty(i => i.AddedAt, T0));

            await db.GetService<IMigrator>().MigrateAsync(MigrationBefore);
        }

        var rows = await LegacyRowsAsync(connectionString);

        Assert.Equal(
            [("alice", "AAPL"), ("alice", "KO"), ("alice", "MSFT"), ("bob", "AAPL")],
            rows.Select(x => (x.UserId, x.Symbol)).Order().ToArray());

        var aliceAapl = rows.Single(x => x is { UserId: "alice", Symbol: "AAPL" });
        Assert.Equal(earliestAaplId, aliceAapl.Id);
        Assert.Equal(T0, aliceAapl.AddedAt);

        // And forward again: one default list per user with their symbols.
        await using (var db = IntegrationFixture.CreateDbContext(connectionString))
        {
            await db.GetService<IMigrator>().MigrateAsync();

            var lists = await db.Watchlists.Include(x => x.Items).OrderBy(x => x.UserId).ToListAsync();
            Assert.Equal(2, lists.Count);
            Assert.Equal(["AAPL", "KO", "MSFT"], lists[0].Items.Select(x => x.Symbol).Order());
            Assert.Equal("AAPL", lists[0].Items.OrderBy(x => x.SortOrder).First().Symbol);
        }
    }

    // Raw SQL in the pre-migration shape: one row per (UserId, Symbol).
    private static async Task<Guid> SeedLegacyRowAsync(
        string connectionString, string userId, string symbol, DateTimeOffset addedAt)
    {
        var id = Guid.NewGuid();

        await using var db = IntegrationFixture.CreateDbContext(connectionString);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO [Watchlists] ([Id], [UserId], [Symbol], [AddedAt]) VALUES ({id}, {userId}, {symbol}, {addedAt})");

        return id;
    }

    private static async Task<List<LegacyRow>> LegacyRowsAsync(string connectionString)
    {
        await using var db = IntegrationFixture.CreateDbContext(connectionString);

        return await db.Database
            .SqlQueryRaw<LegacyRow>("SELECT [Id], [UserId], [Symbol], [AddedAt] FROM [Watchlists]")
            .ToListAsync();
    }

    private sealed record LegacyRow(Guid Id, string UserId, string Symbol, DateTimeOffset AddedAt);
}
