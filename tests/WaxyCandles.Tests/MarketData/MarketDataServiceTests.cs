using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WaxyCandles.Application.MarketData.Messages;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure.MarketData;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.MarketData;

/// <summary>
/// The write side of the outbox pattern: each polled price must produce a
/// StockPrice and a matching OutboxMessage in the same SaveChanges.
/// </summary>
public class MarketDataServiceTests(IntegrationFixture fixture)
    : DatabaseTestBase(fixture)
{
    private readonly FakeMarketDataProvider _provider = new();

    [Fact]
    public async Task Each_polled_price_is_written_with_a_matching_outbox_message()
    {
        await SeedAlertAsync("AAPL");
        await SeedWatchlistAsync("MSFT");
        _provider.SetQuote("AAPL", 150m);
        _provider.SetQuote("MSFT", 300m);

        await UpdatePricesAsync();

        await using var db = NewDbContext();
        var prices = await db.StockPrices.ToListAsync();
        var outbox = await db.OutboxMessages.ToListAsync();

        Assert.Equal(2, prices.Count);
        Assert.Equal(2, outbox.Count);

        foreach (var price in prices)
        {
            var (entry, message) = Assert.Single(
                outbox.Select(o => (Entry: o, Message: Deserialize(o))),
                x => x.Message.Symbol == price.Symbol);

            Assert.Equal(nameof(StockPriceUpdatedMessage), entry.Type);
            Assert.Null(entry.ProcessedAt);
            Assert.Equal(entry.Id, message.MessageId);
            Assert.Equal(price.Price, message.Price);
            Assert.Equal(price.Timestamp, message.Timestamp);
            Assert.Equal(price.Timestamp, entry.CreatedAt);
        }

        var aapl = prices.Single(p => p.Symbol == "AAPL");
        Assert.Equal(150m, aapl.Price);
        Assert.Equal(1.5m, aapl.Change);
        Assert.Equal(0.75m, aapl.ChangePercent);
    }

    [Fact]
    public async Task Polls_untriggered_alert_and_watchlist_symbols_once_each()
    {
        await SeedAlertAsync("AAPL");
        await SeedAlertAsync("AAPL", userId: "user-2");
        await SeedAlertAsync("TSLA", isTriggered: true);
        await SeedWatchlistAsync("AAPL");
        await SeedWatchlistAsync("MSFT");

        await UpdatePricesAsync();

        Assert.Equal(
            ["AAPL", "MSFT"],
            _provider.QuoteRequests.Order().ToArray());
    }

    [Fact]
    public async Task Symbol_with_no_quote_is_skipped_without_affecting_others()
    {
        await SeedAlertAsync("AAPL");
        await SeedWatchlistAsync("MSFT");
        _provider.Quotes["AAPL"] = null;
        _provider.SetQuote("MSFT", 300m);

        await UpdatePricesAsync();

        await using var db = NewDbContext();
        Assert.Equal(["MSFT"], await db.StockPrices.Select(p => p.Symbol).ToListAsync());
        Assert.Equal(1, await db.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task Provider_failure_on_one_symbol_discards_the_whole_cycle()
    {
        // Current behavior: all writes are staged and saved once at the end, so a
        // provider exception (e.g. an Alpha Vantage rate limit) on any symbol
        // throws away prices already fetched for earlier symbols in the cycle.
        await SeedAlertAsync("AAPL");
        await SeedWatchlistAsync("MSFT");
        _provider.SetQuote("AAPL", 150m);
        _provider.FailingSymbols.Add("MSFT");

        await Assert.ThrowsAsync<HttpRequestException>(UpdatePricesAsync);

        Assert.Contains("AAPL", _provider.QuoteRequests);

        await using var db = NewDbContext();
        Assert.Equal(0, await db.StockPrices.CountAsync());
        Assert.Equal(0, await db.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task Nothing_is_polled_when_no_one_is_watching_anything()
    {
        await SeedAlertAsync("TSLA", isTriggered: true);

        await UpdatePricesAsync();

        Assert.Empty(_provider.QuoteRequests);

        await using var db = NewDbContext();
        Assert.Equal(0, await db.StockPrices.CountAsync());
        Assert.Equal(0, await db.OutboxMessages.CountAsync());
    }

    private async Task UpdatePricesAsync()
    {
        await using var db = NewDbContext();
        await new MarketDataService(db, _provider).UpdatePricesAsync();
    }

    private static StockPriceUpdatedMessage Deserialize(OutboxMessage entry) =>
        JsonSerializer.Deserialize<StockPriceUpdatedMessage>(entry.Payload)!;

    private async Task SeedAlertAsync(
        string symbol,
        string userId = "user-1",
        bool isTriggered = false)
    {
        await using var db = NewDbContext();
        db.Alerts.Add(new Alert
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Symbol = symbol,
            Direction = AlertDirection.Above,
            TargetPrice = 100m,
            IsTriggered = isTriggered
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedWatchlistAsync(string symbol)
    {
        await using var db = NewDbContext();
        db.Watchlists.Add(new Watchlist
        {
            Id = Guid.NewGuid(),
            UserId = "user-1",
            Symbol = symbol
        });
        await db.SaveChangesAsync();
    }
}
