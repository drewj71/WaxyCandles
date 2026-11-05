using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WaxyCandles.Api.Middleware;
using WaxyCandles.Application.MarketData.Exceptions;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Api;

/// <summary>
/// POST /api/backtests through the real API and database, with only the market
/// data provider faked: stored candles are used as they are, a gap costs
/// exactly one provider request, and the endpoint shares the history import's
/// rate limit.
/// </summary>
public class BacktestApiTests(IntegrationFixture fixture) : ApiTestBase(fixture)
{
    // Monday. Every date used here is long past, so "today" never matters.
    private static readonly DateOnly Monday = new(2024, 1, 1);

    private static object Request(
        DateOnly from,
        DateOnly to,
        string symbol = "AAPL",
        object? extra = null) =>
        new Dictionary<string, object?>
        {
            ["symbol"] = symbol,
            ["from"] = from.ToString("yyyy-MM-dd"),
            ["to"] = to.ToString("yyyy-MM-dd"),
            ["strategy"] = "MovingAverageCrossover",
            ["movingAverageCrossover"] = new { fastPeriod = 2, slowPeriod = 3, movingAverageType = "Sma" }
        }
        .Concat(extra is null
            ? []
            : JsonSerializer.SerializeToElement(extra, Json).EnumerateObject()
                .Select(p => KeyValuePair.Create(p.Name, (object?)p.Value)))
        .GroupBy(x => x.Key)
        .ToDictionary(x => x.Key, x => x.Last().Value);

    [Fact]
    public async Task Requires_sign_in()
    {
        var response = await NewClient().Http.PostAsJsonAsync(
            "/api/backtests",
            Request(Monday, Monday.AddDays(30)),
            Json);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(MarketData.DailyCandleRequests);
    }

    [Fact]
    public async Task Invalid_input_is_400_with_field_errors_and_no_provider_call()
    {
        var client = await RegisteredClientAsync("bt-invalid@example.com");

        var response = await client.Http.PostAsJsonAsync(
            "/api/backtests",
            Request(Monday.AddDays(10), Monday, symbol: "not a symbol", extra: new
            {
                movingAverageCrossover = new { fastPeriod = 50, slowPeriod = 20 },
                slippageBps = 1000
            }),
            Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = json.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("symbol", out _));
        Assert.True(errors.TryGetProperty("from", out _));
        Assert.True(errors.TryGetProperty("movingAverageCrossover.slowPeriod", out _));
        Assert.True(errors.TryGetProperty("slippageBps", out _));
        Assert.Empty(MarketData.DailyCandleRequests);
    }

    [Fact]
    public async Task A_range_over_20_years_is_400()
    {
        var client = await RegisteredClientAsync("bt-range@example.com");

        var response = await client.Http.PostAsJsonAsync(
            "/api/backtests",
            Request(new DateOnly(2000, 1, 1), new DateOnly(2024, 1, 1)),
            Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("at most 20 years", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_unknown_strategy_is_400()
    {
        var client = await RegisteredClientAsync("bt-strategy@example.com");

        var response = await client.Http.PostAsJsonAsync(
            "/api/backtests",
            Request(Monday, Monday.AddDays(30), extra: new { strategy = "Martingale" }),
            Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(MarketData.DailyCandleRequests);
    }

    [Fact]
    public async Task Stored_candles_are_used_without_a_provider_call()
    {
        // Four weeks of weekdays: 2024-01-01 to 2024-01-26.
        await SeedAsync(Weekdays(Monday, 20, close: i => 100m + (i % 7) * 3m));
        var client = await RegisteredClientAsync("bt-stored@example.com");

        var response = await client.Http.PostAsJsonAsync(
            "/api/backtests",
            Request(Monday, new DateOnly(2024, 1, 26)),
            Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(MarketData.DailyCandleRequests);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.Equal("AAPL", root.GetProperty("symbol").GetString());
        Assert.Equal("MovingAverageCrossover", root.GetProperty("strategy").GetString());
        Assert.Equal("2024-01-01", root.GetProperty("from").GetString());
        Assert.Equal("2024-01-26", root.GetProperty("to").GetString());
        Assert.Equal(20, root.GetProperty("barCount").GetInt32());
        Assert.Equal(3, root.GetProperty("warmupBars").GetInt32());
        Assert.False(root.GetProperty("dataImported").GetBoolean());
        Assert.Equal(10_000m, root.GetProperty("costs").GetProperty("initialCapital").GetDecimal());

        foreach (var metrics in new[] { root.GetProperty("metrics"), root.GetProperty("benchmark") })
        {
            foreach (var name in new[]
                     {
                         "finalEquity", "totalReturnPercent", "cagrPercent", "maxDrawdownPercent",
                         "maxDrawdownPeakDate", "maxDrawdownTroughDate", "sharpeRatio", "tradeCount",
                         "winRatePercent", "averageTradeReturnPercent", "exposurePercent", "hasOpenPosition"
                     })
            {
                Assert.True(metrics.TryGetProperty(name, out _), name);
            }
        }

        // Buy and hold is always computed, and holds from the second bar.
        Assert.True(root.GetProperty("benchmark").GetProperty("hasOpenPosition").GetBoolean());

        var curve = root.GetProperty("equityCurve");
        Assert.Equal(20, curve.GetArrayLength());
        Assert.Equal("2024-01-01", curve[0].GetProperty("date").GetString());
        Assert.Equal(10_000m, curve[0].GetProperty("strategy").GetDecimal());
        Assert.Equal(10_000m, curve[0].GetProperty("benchmark").GetDecimal());

        var trades = root.GetProperty("trades");
        Assert.True(trades.GetArrayLength() > 0);
        foreach (var name in new[] { "entryDate", "entryPrice", "exitDate", "exitPrice", "shares", "profitLoss", "returnPercent", "isOpen" })
        {
            Assert.True(trades[0].TryGetProperty(name, out _), name);
        }

        var candles = root.GetProperty("candles");
        Assert.Equal(20, candles.GetArrayLength());
        Assert.Equal("2024-01-01", candles[0].GetProperty("date").GetString());
        Assert.Equal(100m, candles[0].GetProperty("close").GetDecimal());
    }

    [Fact]
    public async Task A_missing_range_is_imported_with_exactly_one_provider_request()
    {
        // Stored: only the last week. The provider has all four.
        var all = Weekdays(Monday, 20, close: i => 100m + i);
        await SeedAsync(all.TakeLast(5));
        MarketData.Candles = all;
        var client = await RegisteredClientAsync("bt-import@example.com");

        var response = await client.Http.PostAsJsonAsync(
            "/api/backtests",
            Request(Monday, new DateOnly(2024, 1, 26)),
            Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var request = Assert.Single(MarketData.DailyCandleRequests);
        Assert.Equal(("AAPL", Monday, new DateOnly(2024, 1, 26)), request);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("dataImported").GetBoolean());
        Assert.Equal(20, json.RootElement.GetProperty("barCount").GetInt32());

        await using (var db = NewDbContext())
        {
            Assert.Equal(20, await db.StockCandles.CountAsync(x => x.Symbol == "AAPL" && x.Interval == CandleInterval.OneDay));
        }

        // Now covered: running it again costs nothing.
        var again = await client.Http.PostAsJsonAsync(
            "/api/backtests",
            Request(Monday, new DateOnly(2024, 1, 26)),
            Json);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Single(MarketData.DailyCandleRequests);
    }

    [Fact]
    public async Task Provider_history_that_starts_later_still_costs_one_request_per_backtest()
    {
        // The symbol "listed" in the third week; the range asks for four.
        MarketData.Candles = Weekdays(Monday.AddDays(14), 10, close: i => 50m + i);
        var client = await RegisteredClientAsync("bt-listed@example.com");

        var response = await client.Http.PostAsJsonAsync(
            "/api/backtests",
            Request(Monday, new DateOnly(2024, 1, 26)),
            Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(MarketData.DailyCandleRequests);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("2024-01-15", json.RootElement.GetProperty("from").GetString());
        Assert.Equal("2024-01-01", json.RootElement.GetProperty("requestedFrom").GetString());
    }

    [Fact]
    public async Task No_data_at_all_is_404_after_one_provider_request()
    {
        var client = await RegisteredClientAsync("bt-none@example.com");

        var response = await client.Http.PostAsJsonAsync(
            "/api/backtests",
            Request(Monday, new DateOnly(2024, 1, 26), symbol: "ZZZZ"),
            Json);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("No daily candles for ZZZZ", await response.Content.ReadAsStringAsync());
        Assert.Single(MarketData.DailyCandleRequests);
    }

    [Theory]
    [InlineData(MarketDataUnavailableReason.Timeout, HttpStatusCode.GatewayTimeout)]
    [InlineData(MarketDataUnavailableReason.RateLimited, HttpStatusCode.ServiceUnavailable)]
    public async Task Vendor_failures_during_the_import_map_to_504_and_503(
        MarketDataUnavailableReason reason,
        HttpStatusCode expected)
    {
        MarketData.Throw = new MarketDataUnavailableException(reason, "Market Data", TimeSpan.FromMinutes(5));
        var client = await RegisteredClientAsync("bt-vendor@example.com");

        var response = await client.Http.PostAsJsonAsync(
            "/api/backtests",
            Request(Monday, new DateOnly(2024, 1, 26)),
            Json);

        Assert.Equal(expected, response.StatusCode);

        if (expected == HttpStatusCode.ServiceUnavailable)
        {
            Assert.Contains(ExceptionHandlingMiddleware.MarketDataRateLimitedMessage, await response.Content.ReadAsStringAsync());
            Assert.Equal("300", response.Headers.GetValues("Retry-After").Single());
        }
    }

    [Fact]
    public async Task Backtests_share_the_history_import_limit()
    {
        var settings = Settings();
        settings["RateLimiting:HistoryImport:PermitLimit"] = "2";
        settings["RateLimiting:HistoryImport:Window"] = "00:10:00";

        await SeedAsync(Weekdays(Monday, 20, close: i => 100m + i));

        await using var limited = new WaxyCandlesApiFactory(settings, MarketData);
        var alice = NewClient(limited);
        (await RegisterAsync(alice.Http, "bt-limit@example.com")).EnsureSuccessStatusCode();

        // One history import and one backtest use up the shared allowance.
        Assert.Equal(
            HttpStatusCode.OK,
            (await alice.Http.PostAsync("/api/stocks/AAPL/historical?interval=OneDay", null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await alice.Http.PostAsJsonAsync("/api/backtests", Request(Monday, new DateOnly(2024, 1, 26)), Json)).StatusCode);

        var rejected = await alice.Http.PostAsJsonAsync("/api/backtests", Request(Monday, new DateOnly(2024, 1, 26)), Json);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.Contains("Retry-After"));

        // Another user has their own allowance.
        var bob = NewClient(limited);
        (await RegisterAsync(bob.Http, "bt-limit-bob@example.com")).EnsureSuccessStatusCode();
        Assert.Equal(
            HttpStatusCode.OK,
            (await bob.Http.PostAsJsonAsync("/api/backtests", Request(Monday, new DateOnly(2024, 1, 26)), Json)).StatusCode);
    }

    private async Task SeedAsync(IEnumerable<StockCandle> candles)
    {
        await using var db = NewDbContext();
        db.StockCandles.AddRange(candles.Select(c => new StockCandle
        {
            Id = Guid.NewGuid(),
            Symbol = c.Symbol,
            Interval = c.Interval,
            Timestamp = c.Timestamp,
            Open = c.Open,
            High = c.High,
            Low = c.Low,
            Close = c.Close,
            Volume = c.Volume
        }));
        await db.SaveChangesAsync();
    }

    /// <summary><paramref name="count"/> weekday candles from <paramref name="start"/>, at UTC midnight like the providers store them.</summary>
    private static List<StockCandle> Weekdays(DateOnly start, int count, Func<int, decimal> close)
    {
        var candles = new List<StockCandle>();

        for (var day = start; candles.Count < count; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            var price = close(candles.Count);

            candles.Add(new StockCandle
            {
                Id = Guid.NewGuid(),
                Symbol = "AAPL",
                Interval = CandleInterval.OneDay,
                Timestamp = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                Open = price,
                High = price + 1m,
                Low = price - 1m,
                Close = price,
                Volume = 1_000
            });
        }

        return candles;
    }
}
