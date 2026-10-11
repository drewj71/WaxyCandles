using Microsoft.EntityFrameworkCore;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure.MarketData;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.MarketData;

public class HistoricalMarketDataServiceTests(IntegrationFixture fixture)
    : DatabaseTestBase(fixture)
{
    private static readonly DateTimeOffset Day1 = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeMarketDataProvider _provider = new();

    [Fact]
    public async Task First_import_stores_every_candle()
    {
        _provider.Candles = Days("AAPL", CandleInterval.OneDay, 0, 1, 2);

        await ImportAsync("AAPL", CandleInterval.OneDay);

        Assert.Equal(
            [Day1, Day1.AddDays(1), Day1.AddDays(2)],
            await StoredTimestampsAsync("AAPL", CandleInterval.OneDay));
    }

    [Fact]
    public async Task Re_importing_overlapping_candles_only_inserts_new_ones()
    {
        _provider.Candles = Days("AAPL", CandleInterval.OneDay, 0, 1, 2);
        await ImportAsync("AAPL", CandleInterval.OneDay);

        _provider.Candles = Days("AAPL", CandleInterval.OneDay, 1, 2, 3, 4);
        await ImportAsync("AAPL", CandleInterval.OneDay);

        Assert.Equal(
            Enumerable.Range(0, 5).Select(d => Day1.AddDays(d)).ToList(),
            await StoredTimestampsAsync("AAPL", CandleInterval.OneDay));
    }

    [Fact]
    public async Task Existing_candles_are_not_updated_by_a_re_import()
    {
        // Current behavior: dedup is by timestamp only, so a candle that was
        // still forming when first imported (e.g. today's) keeps its first values.
        _provider.Candles = Days("AAPL", CandleInterval.OneDay, 0);
        _provider.Candles[0].Close = 100m;
        await ImportAsync("AAPL", CandleInterval.OneDay);

        _provider.Candles[0].Close = 105m;
        await ImportAsync("AAPL", CandleInterval.OneDay);

        await using var db = NewDbContext();
        var stored = await db.StockCandles.SingleAsync();
        Assert.Equal(100m, stored.Close);
    }

    [Fact]
    public async Task Same_timestamp_is_stored_separately_per_interval_and_symbol()
    {
        _provider.Candles = Days("AAPL", CandleInterval.OneDay, 0);
        await ImportAsync("AAPL", CandleInterval.OneDay);

        _provider.Candles = Days("AAPL", CandleInterval.OneHour, 0);
        await ImportAsync("AAPL", CandleInterval.OneHour);

        _provider.Candles = Days("MSFT", CandleInterval.OneDay, 0);
        await ImportAsync("MSFT", CandleInterval.OneDay);

        await using var db = NewDbContext();
        Assert.Equal(3, await db.StockCandles.CountAsync());
    }

    [Fact]
    public async Task Dedup_lookup_normalizes_the_requested_symbol()
    {
        _provider.Candles = Days("AAPL", CandleInterval.OneDay, 0, 1);
        await ImportAsync("AAPL", CandleInterval.OneDay);

        await ImportAsync("  aapl ", CandleInterval.OneDay);

        await using var db = NewDbContext();
        Assert.Equal(2, await db.StockCandles.CountAsync());
    }

    [Fact]
    public async Task Database_unique_index_rejects_duplicate_candles_that_bypass_the_service()
    {
        await using var db = NewDbContext();

        db.StockCandles.AddRange(Days("AAPL", CandleInterval.OneDay, 0, 0));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Stored_candles_are_returned_oldest_first_for_the_requested_interval()
    {
        await using (var db = NewDbContext())
        {
            db.StockCandles.AddRange(Days("AAPL", CandleInterval.OneDay, 2, 0, 1));
            db.StockCandles.AddRange(Days("AAPL", CandleInterval.OneHour, 5));
            await db.SaveChangesAsync();
        }

        await using var readDb = NewDbContext();
        var candles = await new HistoricalMarketDataService(readDb, _provider)
            .GetHistoricalCandlesAsync(" aapl ", CandleInterval.OneDay);

        Assert.Equal(
            [Day1, Day1.AddDays(1), Day1.AddDays(2)],
            candles.Select(c => c.Timestamp).ToList());
    }

    [Fact]
    public async Task Stock_details_use_the_latest_stored_price()
    {
        await using (var db = NewDbContext())
        {
            db.StockPrices.AddRange(
                new StockPrice { Id = Guid.NewGuid(), Symbol = "AAPL", Price = 100m, Timestamp = Day1 },
                new StockPrice { Id = Guid.NewGuid(), Symbol = "AAPL", Price = 120m, Timestamp = Day1.AddHours(1) },
                new StockPrice { Id = Guid.NewGuid(), Symbol = "MSFT", Price = 999m, Timestamp = Day1.AddHours(2) });
            await db.SaveChangesAsync();
        }

        await using var readDb = NewDbContext();
        var details = await new HistoricalMarketDataService(readDb, _provider)
            .GetStockDetailsAsync("aapl", CandleInterval.OneDay);

        Assert.Equal("AAPL", details.Symbol);
        Assert.NotNull(details.Quote);
        Assert.Equal(120m, details.Quote.Price);
    }

    [Fact]
    public async Task Stock_details_have_no_quote_when_no_price_is_stored()
    {
        await using var db = NewDbContext();
        var details = await new HistoricalMarketDataService(db, _provider)
            .GetStockDetailsAsync("AAPL", CandleInterval.OneDay);

        Assert.Null(details.Quote);
        Assert.Empty(details.HistoricalCandles);
    }

    private async Task ImportAsync(string symbol, CandleInterval interval)
    {
        await using var db = NewDbContext();
        await new HistoricalMarketDataService(db, _provider)
            .ImportHistoricalCandlesAsync(symbol, interval);
    }

    private async Task<List<DateTimeOffset>> StoredTimestampsAsync(
        string symbol,
        CandleInterval interval)
    {
        await using var db = NewDbContext();
        return await db.StockCandles
            .Where(c => c.Symbol == symbol && c.Interval == interval)
            .OrderBy(c => c.Timestamp)
            .Select(c => c.Timestamp)
            .ToListAsync();
    }

    private static List<StockCandle> Days(
        string symbol,
        CandleInterval interval,
        params int[] dayOffsets) =>
        dayOffsets
            .Select(d => new StockCandle
            {
                Id = Guid.NewGuid(),
                Symbol = symbol,
                Interval = interval,
                Timestamp = Day1.AddDays(d),
                Open = 100m,
                High = 110m,
                Low = 90m,
                Close = 105m,
                Volume = 1_000
            })
            .ToList();
}
