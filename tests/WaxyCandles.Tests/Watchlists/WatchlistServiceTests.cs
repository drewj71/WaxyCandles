using Microsoft.EntityFrameworkCore;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Infrastructure.Watchlists;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Watchlists;

public class WatchlistServiceTests(IntegrationFixture fixture)
    : DatabaseTestBase(fixture)
{
    private const string UserId = "user-1";

    [Fact]
    public async Task Adding_a_symbol_stores_it_normalized()
    {
        await AddAsync(UserId, "  aapl ");

        Assert.Equal(["AAPL"], await GetWatchlistAsync(UserId));
    }

    [Fact]
    public async Task Adding_the_same_symbol_twice_is_a_no_op()
    {
        await AddAsync(UserId, "AAPL");
        await AddAsync(UserId, "AAPL");

        Assert.Equal(["AAPL"], await GetWatchlistAsync(UserId));
    }

    [Fact]
    public async Task Duplicate_detection_ignores_case_and_whitespace()
    {
        await AddAsync(UserId, "AAPL");
        await AddAsync(UserId, " aapl ");

        Assert.Equal(["AAPL"], await GetWatchlistAsync(UserId));
    }

    [Fact]
    public async Task Different_users_can_watch_the_same_symbol()
    {
        await AddAsync("user-1", "AAPL");
        await AddAsync("user-2", "AAPL");

        Assert.Equal(["AAPL"], await GetWatchlistAsync("user-1"));
        Assert.Equal(["AAPL"], await GetWatchlistAsync("user-2"));
    }

    [Fact]
    public async Task Database_unique_index_rejects_duplicates_that_bypass_the_service()
    {
        // The service's AnyAsync check is check-then-insert. This proves the
        // unique (UserId, Symbol) index is the real guarantee behind it.
        await using var db = NewDbContext();

        db.Watchlists.Add(NewItem(UserId, "AAPL"));
        db.Watchlists.Add(NewItem(UserId, "AAPL"));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Concurrent_duplicate_adds_leave_exactly_one_row()
    {
        // Both calls may pass the existence check before either inserts. The
        // unique index must then reject the loser, so storage stays consistent.
        // The losing caller currently sees a DbUpdateException.
        var attempts = Enumerable.Range(0, 5)
            .Select(_ => Task.Run(async () =>
            {
                try
                {
                    await AddAsync(UserId, "AAPL");
                    return null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            }))
            .ToArray();

        var errors = (await Task.WhenAll(attempts)).OfType<Exception>().ToList();

        Assert.All(errors, ex => Assert.IsType<DbUpdateException>(ex));
        Assert.Equal(["AAPL"], await GetWatchlistAsync(UserId));
    }

    [Fact]
    public async Task Removing_a_symbol_matches_case_insensitively()
    {
        await AddAsync(UserId, "AAPL");
        await AddAsync(UserId, "MSFT");

        await using (var db = NewDbContext())
        {
            await new WatchlistService(db).RemoveFromWatchlistAsync(UserId, " aapl ");
        }

        Assert.Equal(["MSFT"], await GetWatchlistAsync(UserId));
    }

    private async Task AddAsync(string userId, string symbol)
    {
        await using var db = NewDbContext();
        await new WatchlistService(db).AddToWatchlistAsync(userId, symbol);
    }

    private async Task<IReadOnlyList<string>> GetWatchlistAsync(string userId)
    {
        await using var db = NewDbContext();
        return await new WatchlistService(db).GetWatchlistAsync(userId);
    }

    private static Watchlist NewItem(string userId, string symbol) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Symbol = symbol
    };
}
