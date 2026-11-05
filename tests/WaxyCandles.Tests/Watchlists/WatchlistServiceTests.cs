using Microsoft.EntityFrameworkCore;
using WaxyCandles.Application.Common.Exceptions;
using WaxyCandles.Application.Watchlists.Models;
using WaxyCandles.Application.Watchlists.Validation;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure.Watchlists;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Watchlists;

public class WatchlistServiceTests(IntegrationFixture fixture)
    : DatabaseTestBase(fixture)
{
    private const string UserId = "user-1";
    private const string OtherUserId = "user-2";

    // ---- Single-list API (default list) ----

    [Fact]
    public async Task Adding_a_symbol_stores_it_normalized_in_a_new_default_list()
    {
        await AddAsync(UserId, "  aapl ");

        Assert.Equal(["AAPL"], await GetDefaultAsync(UserId));

        var list = Assert.Single(await Run(s => s.GetWatchlistsAsync(UserId)));
        Assert.Equal(WatchlistRules.DefaultListName, list.Name);
    }

    [Fact]
    public async Task Adding_the_same_symbol_twice_is_a_no_op()
    {
        await AddAsync(UserId, "AAPL");
        await AddAsync(UserId, "AAPL");

        Assert.Equal(["AAPL"], await GetDefaultAsync(UserId));
    }

    [Fact]
    public async Task Duplicate_detection_ignores_case_and_whitespace()
    {
        await AddAsync(UserId, "AAPL");
        await AddAsync(UserId, " aapl ");

        Assert.Equal(["AAPL"], await GetDefaultAsync(UserId));
    }

    [Fact]
    public async Task Different_users_can_watch_the_same_symbol()
    {
        await AddAsync(UserId, "AAPL");
        await AddAsync(OtherUserId, "AAPL");

        Assert.Equal(["AAPL"], await GetDefaultAsync(UserId));
        Assert.Equal(["AAPL"], await GetDefaultAsync(OtherUserId));
    }

    [Fact]
    public async Task Invalid_symbol_is_rejected()
    {
        var error = await Assert.ThrowsAsync<RequestValidationException>(() => AddAsync(UserId, "NOT A SYMBOL"));

        Assert.Contains("symbol", error.Errors.Keys);
        Assert.Empty(await GetDefaultAsync(UserId));
    }

    [Fact]
    public async Task Database_unique_index_rejects_a_symbol_twice_in_one_list()
    {
        // The service's check is check-then-insert. This proves the unique
        // (WatchlistId, Symbol) index is the real guarantee behind it.
        var listId = await WatchlistSeed.AddAsync(ConnectionString, UserId, "AAPL");

        await using var db = NewDbContext();
        db.WatchlistItems.Add(new WatchlistItem { Id = Guid.NewGuid(), WatchlistId = listId, Symbol = "AAPL" });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Database_unique_index_rejects_two_lists_with_one_name_for_a_user()
    {
        await using var db = NewDbContext();
        db.Watchlists.Add(new Watchlist { Id = Guid.NewGuid(), UserId = UserId, Name = "Tech" });
        db.Watchlists.Add(new Watchlist { Id = Guid.NewGuid(), UserId = UserId, Name = "TECH" });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Concurrent_first_adds_leave_one_default_list_with_each_symbol_once()
    {
        // Every call may find no list and try to create one, and may pass the
        // symbol check before another inserts. The unique indexes reject the
        // losers, and the service treats that as done rather than an error.
        var attempts = new[] { "AAPL", "AAPL", "AAPL", "MSFT", "MSFT" }
            .Select(symbol => Task.Run(() => AddAsync(UserId, symbol)))
            .ToArray();

        await Task.WhenAll(attempts);

        var list = Assert.Single(await Run(s => s.GetWatchlistsAsync(UserId)));
        Assert.Equal(["AAPL", "MSFT"], list.Symbols.Order().ToArray());
    }

    [Fact]
    public async Task Removing_a_symbol_matches_case_insensitively()
    {
        await AddAsync(UserId, "AAPL");
        await AddAsync(UserId, "MSFT");

        await Run(s => s.RemoveFromDefaultWatchlistAsync(UserId, " aapl "));

        Assert.Equal(["MSFT"], await GetDefaultAsync(UserId));
    }

    [Fact]
    public async Task Default_list_is_the_first_by_sort_order()
    {
        var first = await CreateAsync(UserId, "First");
        var second = await CreateAsync(UserId, "Second");
        await Run(s => s.AddSymbolAsync(UserId, second.Id, "MSFT"));

        await AddAsync(UserId, "AAPL");
        Assert.Equal(["AAPL"], (await GetAsync(UserId, first.Id))!.Symbols);

        await Run(s => s.ReorderWatchlistsAsync(UserId, [second.Id, first.Id]));
        Assert.Equal(["MSFT"], await GetDefaultAsync(UserId));
    }

    // ---- Lists ----

    [Fact]
    public async Task Lists_are_returned_in_sort_order_with_symbols_in_list_order()
    {
        var a = await CreateAsync(UserId, "  Tech  ");
        var b = await CreateAsync(UserId, "Dividends");

        await Run(s => s.AddSymbolAsync(UserId, a.Id, "msft"));
        await Run(s => s.AddSymbolAsync(UserId, a.Id, "AAPL"));
        await Run(s => s.AddSymbolAsync(UserId, b.Id, "KO"));

        var lists = await Run(s => s.GetWatchlistsAsync(UserId));

        Assert.Equal(["Tech", "Dividends"], lists.Select(x => x.Name));
        Assert.Equal(["MSFT", "AAPL"], lists[0].Symbols);
        Assert.Equal(["KO"], lists[1].Symbols);
    }

    [Fact]
    public async Task List_names_are_unique_per_user_ignoring_case()
    {
        await CreateAsync(UserId, "Tech");

        var error = await Assert.ThrowsAsync<RequestValidationException>(() => CreateAsync(UserId, "tech"));
        Assert.Contains("name", error.Errors.Keys);

        // Another user can use the name.
        await CreateAsync(OtherUserId, "Tech");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Blank_name_is_rejected(string? name)
    {
        await Assert.ThrowsAsync<RequestValidationException>(() => CreateAsync(UserId, name));
    }

    [Fact]
    public async Task Name_longer_than_the_limit_is_rejected()
    {
        await Assert.ThrowsAsync<RequestValidationException>(() =>
            CreateAsync(UserId, new string('x', Watchlist.NameMaxLength + 1)));
    }

    [Fact]
    public async Task A_user_can_have_at_most_the_list_limit()
    {
        for (var i = 0; i < WatchlistRules.MaxListsPerUser; i++)
        {
            await CreateAsync(UserId, $"List {i}");
        }

        await Assert.ThrowsAsync<RequestValidationException>(() => CreateAsync(UserId, "One too many"));

        // The limit is per user.
        await CreateAsync(OtherUserId, "Fine");
    }

    [Fact]
    public async Task A_list_holds_at_most_the_symbol_limit()
    {
        var list = await CreateAsync(UserId, "Big");

        for (var i = 0; i < WatchlistRules.MaxSymbolsPerList; i++)
        {
            await Run(s => s.AddSymbolAsync(UserId, list.Id, $"S{i}"));
        }

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            Run(s => s.AddSymbolAsync(UserId, list.Id, "EXTRA")));

        // Re-adding one already there is still a no-op, not an error.
        await Run(s => s.AddSymbolAsync(UserId, list.Id, "S0"));
    }

    [Fact]
    public async Task Rename_changes_the_name_and_rejects_a_duplicate()
    {
        var tech = await CreateAsync(UserId, "Tech");
        await CreateAsync(UserId, "Energy");

        var renamed = await Run(s => s.RenameWatchlistAsync(UserId, tech.Id, " Semis "));
        Assert.Equal("Semis", renamed!.Name);

        // Same name, different case, on itself: allowed.
        Assert.Equal("SEMIS", (await Run(s => s.RenameWatchlistAsync(UserId, tech.Id, "SEMIS")))!.Name);

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            Run(s => s.RenameWatchlistAsync(UserId, tech.Id, "energy")));
    }

    [Fact]
    public async Task Deleting_a_list_deletes_its_symbols()
    {
        var list = await CreateAsync(UserId, "Tech");
        await Run(s => s.AddSymbolAsync(UserId, list.Id, "AAPL"));

        Assert.True(await Run(s => s.DeleteWatchlistAsync(UserId, list.Id)));
        Assert.False(await Run(s => s.DeleteWatchlistAsync(UserId, list.Id)));

        await using var db = NewDbContext();
        Assert.Equal(0, await db.WatchlistItems.CountAsync());
    }

    [Fact]
    public async Task Reordering_lists_needs_every_id_exactly_once()
    {
        var a = await CreateAsync(UserId, "A");
        var b = await CreateAsync(UserId, "B");
        var c = await CreateAsync(UserId, "C");
        var others = await CreateAsync(OtherUserId, "Not yours");

        await Run(s => s.ReorderWatchlistsAsync(UserId, [c.Id, a.Id, b.Id]));
        Assert.Equal(["C", "A", "B"], (await Run(s => s.GetWatchlistsAsync(UserId))).Select(x => x.Name));

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            Run(s => s.ReorderWatchlistsAsync(UserId, [a.Id, b.Id])));
        await Assert.ThrowsAsync<RequestValidationException>(() =>
            Run(s => s.ReorderWatchlistsAsync(UserId, [a.Id, a.Id, b.Id])));
        await Assert.ThrowsAsync<RequestValidationException>(() =>
            Run(s => s.ReorderWatchlistsAsync(UserId, [a.Id, b.Id, others.Id])));

        Assert.Equal(["C", "A", "B"], (await Run(s => s.GetWatchlistsAsync(UserId))).Select(x => x.Name));
    }

    [Fact]
    public async Task Reordering_symbols_needs_every_symbol_exactly_once()
    {
        var list = await CreateAsync(UserId, "Tech");
        foreach (var symbol in new[] { "AAPL", "MSFT", "NVDA" })
        {
            await Run(s => s.AddSymbolAsync(UserId, list.Id, symbol));
        }

        var reordered = await Run(s => s.ReorderSymbolsAsync(UserId, list.Id, ["nvda", "AAPL", "MSFT"]));
        Assert.Equal(["NVDA", "AAPL", "MSFT"], reordered!.Symbols);

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            Run(s => s.ReorderSymbolsAsync(UserId, list.Id, ["NVDA", "AAPL"])));
        await Assert.ThrowsAsync<RequestValidationException>(() =>
            Run(s => s.ReorderSymbolsAsync(UserId, list.Id, ["NVDA", "AAPL", "TSLA"])));

        // A symbol added afterwards goes to the end.
        var added = await Run(s => s.AddSymbolAsync(UserId, list.Id, "TSLA"));
        Assert.Equal(["NVDA", "AAPL", "MSFT", "TSLA"], added!.Symbols);
    }

    [Fact]
    public async Task Another_users_list_behaves_as_if_it_did_not_exist()
    {
        var alices = await CreateAsync(UserId, "Alice's");
        await Run(s => s.AddSymbolAsync(UserId, alices.Id, "AAPL"));

        Assert.Null(await GetAsync(OtherUserId, alices.Id));
        Assert.Null(await Run(s => s.RenameWatchlistAsync(OtherUserId, alices.Id, "Mine now")));
        Assert.False(await Run(s => s.DeleteWatchlistAsync(OtherUserId, alices.Id)));
        Assert.Null(await Run(s => s.AddSymbolAsync(OtherUserId, alices.Id, "MSFT")));
        Assert.False(await Run(s => s.RemoveSymbolAsync(OtherUserId, alices.Id, "AAPL")));
        Assert.Null(await Run(s => s.ReorderSymbolsAsync(OtherUserId, alices.Id, ["AAPL"])));
        Assert.Null(await Run(s => s.GetQuotesAsync(OtherUserId, alices.Id)));
        await Assert.ThrowsAsync<RequestValidationException>(() =>
            Run(s => s.ReorderWatchlistsAsync(OtherUserId, [alices.Id])));

        var unchanged = await GetAsync(UserId, alices.Id);
        Assert.Equal("Alice's", unchanged!.Name);
        Assert.Equal(["AAPL"], unchanged.Symbols);
    }

    // ---- Quotes ----

    [Fact]
    public async Task Quotes_come_from_stored_prices_and_daily_candles()
    {
        var now = DateTimeOffset.UtcNow;
        var list = await CreateAsync(UserId, "Tech");
        await Run(s => s.AddSymbolAsync(UserId, list.Id, "AAPL"));
        await Run(s => s.AddSymbolAsync(UserId, list.Id, "NEW"));

        var latestAt = now.AddMinutes(-1);

        await using (var db = NewDbContext())
        {
            db.StockPrices.AddRange(
                // The previous session: outside the day range.
                Price("AAPL", 90m, latestAt.AddHours(-18)),
                // This session.
                Price("AAPL", 101m, latestAt.AddHours(-5)),
                Price("AAPL", 99m, latestAt.AddHours(-2)),
                Price("AAPL", 100m, latestAt, change: 2m, changePercent: 2.04m));

            db.StockCandles.AddRange(
                Candle("AAPL", now.Date.AddDays(-40), 80m, 1_000),    // older than the sparkline
                Candle("AAPL", now.Date.AddDays(-3), 97m, 3_000),
                Candle("AAPL", now.Date.AddDays(-2), 98m, 4_000));

            await db.SaveChangesAsync();
        }

        var quotes = (await Run(s => s.GetQuotesAsync(UserId, list.Id)))!;

        Assert.Equal(["AAPL", "NEW"], quotes.Select(x => x.Symbol));

        var aapl = quotes[0];
        Assert.Equal(100m, aapl.Price);
        Assert.Equal(2m, aapl.Change);
        Assert.Equal(2.04m, aapl.ChangePercent);
        Assert.Equal(98m, aapl.PreviousClose);
        Assert.Equal(latestAt, aapl.PriceTimestamp);
        Assert.Equal(99m, aapl.DayLow);
        Assert.Equal(101m, aapl.DayHigh);
        Assert.Equal(4_000, aapl.Volume);
        Assert.Equal([97m, 98m], aapl.Sparkline.Select(x => x.Close));

        // No stored data yet: listed, everything empty.
        var empty = quotes[1];
        Assert.Null(empty.Price);
        Assert.Null(empty.DayLow);
        Assert.Null(empty.Volume);
        Assert.Empty(empty.Sparkline);
    }

    [Fact]
    public async Task Default_list_quotes_keep_the_single_list_shape()
    {
        await AddAsync(UserId, "AAPL");

        await using (var db = NewDbContext())
        {
            db.StockPrices.Add(Price("AAPL", 150m, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var quote = Assert.Single(await Run(s => s.GetDefaultWatchlistQuotesAsync(UserId)));
        Assert.Equal("AAPL", quote.Symbol);
        Assert.Equal(150m, quote.Price);
    }

    // ---- Helpers ----

    private async Task<T> Run<T>(Func<WatchlistService, Task<T>> action)
    {
        await using var db = NewDbContext();
        return await action(new WatchlistService(db));
    }

    private async Task Run(Func<WatchlistService, Task> action)
    {
        await using var db = NewDbContext();
        await action(new WatchlistService(db));
    }

    private Task AddAsync(string userId, string symbol) =>
        Run(s => s.AddToDefaultWatchlistAsync(userId, symbol));

    private Task<IReadOnlyList<string>> GetDefaultAsync(string userId) =>
        Run(s => s.GetDefaultWatchlistSymbolsAsync(userId));

    private Task<WatchlistDto> CreateAsync(string userId, string? name) =>
        Run(s => s.CreateWatchlistAsync(userId, name));

    private Task<WatchlistDto?> GetAsync(string userId, Guid id) =>
        Run(s => s.GetWatchlistAsync(userId, id));

    private static StockPrice Price(
        string symbol,
        decimal price,
        DateTimeOffset at,
        decimal change = 0m,
        decimal changePercent = 0m) => new()
    {
        Id = Guid.NewGuid(),
        Symbol = symbol,
        Price = price,
        Change = change,
        ChangePercent = changePercent,
        Timestamp = at
    };

    private static StockCandle Candle(string symbol, DateTime date, decimal close, long volume) => new()
    {
        Id = Guid.NewGuid(),
        Symbol = symbol,
        Interval = CandleInterval.OneDay,
        Timestamp = new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)),
        Open = close,
        High = close,
        Low = close,
        Close = close,
        Volume = volume
    };
}
