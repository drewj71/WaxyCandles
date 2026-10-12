using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Migrations;

/// <summary>
/// Runs the real data migration against SQL Server: seed rows the way the old
/// code stored them, apply the migration, check what's left.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class NormalizeDailyCandleTimestampsToUtcTests(IntegrationFixture fixture)
{
    private const string MigrationBefore = "20261005153617_AddStockPriceQuoteFields";

    private static readonly TimeSpan Edt = TimeSpan.FromHours(-4);
    private static readonly TimeSpan Est = TimeSpan.FromHours(-5);

    [Fact]
    public async Task Daily_candles_are_rewritten_to_utc_midnight_of_their_trading_date()
    {
        var connectionString = await fixture.CreateDatabaseAsync(MigrationBefore);

        await SeedAsync(connectionString,
            // Imported on an Eastern machine, summer and winter.
            Daily("AAPL", new DateTimeOffset(2026, 9, 1, 0, 0, 0, Edt), close: 1),
            Daily("AAPL", new DateTimeOffset(2026, 12, 1, 0, 0, 0, Est), close: 2),
            // Already UTC.
            Daily("AAPL", new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero), close: 3));

        var candles = await MigrateAndLoadAsync(connectionString);

        Assert.Equal(
            [
                (new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), 1m),
                (new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero), 3m),
                (new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), 2m)
            ],
            candles.Select(c => (c.Timestamp, c.Close)));

        Assert.All(candles, c => Assert.Equal(TimeSpan.Zero, c.Timestamp.Offset));
    }

    [Fact]
    public async Task Duplicate_trading_days_collapse_to_one_row_preferring_the_utc_one()
    {
        var connectionString = await fixture.CreateDatabaseAsync(MigrationBefore);

        await SeedAsync(connectionString,
            // Same day imported locally and again as UTC: keep the UTC row.
            Daily("MSFT", new DateTimeOffset(2026, 9, 3, 0, 0, 0, Edt), close: 10),
            Daily("MSFT", new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero), close: 11),
            // Same day imported from two different time zones: keep one.
            Daily("MSFT", new DateTimeOffset(2026, 9, 4, 0, 0, 0, Edt), close: 20),
            Daily("MSFT", new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.FromHours(-7)), close: 21));

        var candles = await MigrateAndLoadAsync(connectionString);

        Assert.Equal(2, candles.Count);
        Assert.Equal(new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero), candles[0].Timestamp);
        Assert.Equal(11m, candles[0].Close);
        Assert.Equal(new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero), candles[1].Timestamp);
        Assert.Contains(candles[1].Close, new[] { 20m, 21m });
    }

    [Fact]
    public async Task Intraday_candles_are_left_untouched()
    {
        var connectionString = await fixture.CreateDatabaseAsync(MigrationBefore);

        var intraday = new DateTimeOffset(2026, 9, 1, 16, 0, 0, Edt);
        var candle = Daily("AAPL", intraday, close: 1);
        candle.Interval = CandleInterval.OneHour;
        await SeedAsync(connectionString, candle);

        var stored = Assert.Single(await MigrateAndLoadAsync(connectionString));

        Assert.Equal(intraday, stored.Timestamp);
        Assert.Equal(Edt, stored.Timestamp.Offset);
    }

    private static async Task SeedAsync(string connectionString, params StockCandle[] candles)
    {
        await using var db = IntegrationFixture.CreateDbContext(connectionString);
        db.StockCandles.AddRange(candles);
        await db.SaveChangesAsync();
    }

    private static async Task<List<StockCandle>> MigrateAndLoadAsync(string connectionString)
    {
        await using var db = IntegrationFixture.CreateDbContext(connectionString);

        await db.GetService<IMigrator>().MigrateAsync();

        return await db.StockCandles
            .OrderBy(c => c.Symbol)
            .ThenBy(c => c.Timestamp)
            .ToListAsync();
    }

    private static StockCandle Daily(string symbol, DateTimeOffset timestamp, decimal close) => new()
    {
        Id = Guid.NewGuid(),
        Symbol = symbol,
        Interval = CandleInterval.OneDay,
        Timestamp = timestamp,
        Open = 1,
        High = 1,
        Low = 1,
        Close = close,
        Volume = 1
    };
}
