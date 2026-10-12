using Microsoft.EntityFrameworkCore;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure.MarketData;
using WaxyCandles.Tests.Infrastructure;
using WaxyCandles.Tests.MarketData;

namespace WaxyCandles.Tests.Persistence;

public class DecimalPrecisionModelTests
{
    [Fact]
    public void Every_decimal_column_keeps_six_decimal_places()
    {
        // Building the model doesn't open a connection.
        using var db = IntegrationFixture.CreateDbContext("Server=unused;Database=unused");

        var decimalColumns = db.Model.GetEntityTypes()
            .SelectMany(e => e.GetProperties())
            .Where(p => p.ClrType == typeof(decimal))
            .ToList();

        Assert.NotEmpty(decimalColumns);
        Assert.All(decimalColumns, p => Assert.Equal(
            "decimal(18,6)",
            p.GetColumnType()));
    }
}

/// <summary>
/// Values with more than two decimals must survive a round trip through SQL Server.
/// </summary>
public class DecimalPrecisionRoundTripTests(IntegrationFixture fixture)
    : DatabaseTestBase(fixture)
{
    [Fact]
    public async Task Polled_quote_keeps_full_precision()
    {
        await using (var db = NewDbContext())
        {
            db.Watchlists.Add(new Watchlist { Id = Guid.NewGuid(), UserId = "user-1", Symbol = "AAPL" });
            await db.SaveChangesAsync();
        }

        var provider = new FakeMarketDataProvider();
        provider.Quotes["AAPL"] = new()
        {
            Symbol = "AAPL",
            Price = 150.2534m,
            Change = 1.2109m,
            ChangePercent = 0.8119m
        };

        await using (var db = NewDbContext())
        {
            await new MarketDataService(db, provider).UpdatePricesAsync();
        }

        await using var readDb = NewDbContext();
        var stored = await readDb.StockPrices.SingleAsync();

        Assert.Equal(150.2534m, stored.Price);
        Assert.Equal(1.2109m, stored.Change);
        Assert.Equal(0.8119m, stored.ChangePercent);
    }

    [Fact]
    public async Task Sub_penny_alert_target_and_candle_prices_keep_full_precision()
    {
        var alertId = Guid.NewGuid();
        var candleId = Guid.NewGuid();

        await using (var db = NewDbContext())
        {
            db.Alerts.Add(new Alert
            {
                Id = alertId,
                UserId = "user-1",
                Symbol = "SIRI",
                Direction = AlertDirection.Below,
                TargetPrice = 0.0525m
            });

            db.StockCandles.Add(new StockCandle
            {
                Id = candleId,
                Symbol = "AAPL",
                Interval = CandleInterval.OneDay,
                Timestamp = DateTimeOffset.UtcNow,
                Open = 148.123456m,
                High = 151.5m,
                Low = 147.0001m,
                Close = 150.9999m,
                Volume = 1
            });

            await db.SaveChangesAsync();
        }

        await using var readDb = NewDbContext();
        var alert = await readDb.Alerts.SingleAsync(a => a.Id == alertId);
        var candle = await readDb.StockCandles.SingleAsync(c => c.Id == candleId);

        Assert.Equal(0.0525m, alert.TargetPrice);
        Assert.Equal(148.123456m, candle.Open);
        Assert.Equal(147.0001m, candle.Low);
        Assert.Equal(150.9999m, candle.Close);
    }
}
