using Microsoft.Extensions.Caching.Memory;
using WaxyCandles.Application.Common.Exceptions;
using WaxyCandles.Application.Options.Gex;
using WaxyCandles.Application.Options.Interfaces;
using WaxyCandles.Application.Options.Models;
using WaxyCandles.Infrastructure.Options;
using WaxyCandles.Tests.MarketData;
using MicrosoftOptions = Microsoft.Extensions.Options.Options;

namespace WaxyCandles.Tests.Options;

public class GexServiceTests
{
    // Thursday 2026-10-15, 14:00 EDT.
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T18:00:00Z");

    private readonly FakeMarketDataProvider _marketData = new();
    private readonly FakeOptionsDataProvider _options = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    [Fact]
    public async Task Chain_is_requested_around_the_current_price_from_today()
    {
        _marketData.SetQuote("AAPL", 250.40m);

        await Service().GetGexTableAsync(" aapl ", new GexRequest(Days: 30, StrikeRangePercent: 10));

        var query = Assert.Single(_options.Queries);
        Assert.Equal("AAPL", query.Symbol);
        Assert.Equal(new DateOnly(2026, 10, 15), query.ExpirationFrom);
        Assert.Equal(new DateOnly(2026, 11, 14), query.ExpirationTo);
        // 250.40 -/+ 10%, widened to whole dollars.
        Assert.Equal(225m, query.StrikeMin);
        Assert.Equal(276m, query.StrikeMax);
    }

    [Fact]
    public async Task Table_is_built_from_contracts_inside_the_requested_window_only()
    {
        _marketData.SetQuote("AAPL", 100m);
        _options.Chain = new OptionChain("AAPL", 100m, Now,
        [
            Contract(100m, "2026-10-16"),
            // Outside the strike window (90-110).
            Contract(120m, "2026-10-16"),
            // Past the last expiration (Oct 22).
            Contract(100m, "2026-10-30")
        ]);

        var table = await Service().GetGexTableAsync("AAPL", new GexRequest(Days: 7, StrikeRangePercent: 10));

        Assert.NotNull(table);
        Assert.Equal([100m], table.Rows.Select(r => r.Strike));
        Assert.Equal([new DateOnly(2026, 10, 16)], table.Expirations);
    }

    [Fact]
    public async Task No_quote_returns_null_without_asking_for_a_chain()
    {
        Assert.Null(await Service().GetGexTableAsync("NOPE", new GexRequest()));
        Assert.Empty(_options.Queries);
    }

    [Fact]
    public async Task No_chain_returns_null()
    {
        _marketData.SetQuote("AAPL", 100m);

        Assert.Null(await Service().GetGexTableAsync("AAPL", new GexRequest()));
    }

    [Fact]
    public async Task Repeat_requests_within_the_cache_duration_reuse_the_table()
    {
        _marketData.SetQuote("AAPL", 100m);
        _options.Chain = new OptionChain("AAPL", 100m, Now, [Contract(100m, "2026-10-16")]);
        var service = Service();

        var first = await service.GetGexTableAsync("AAPL", new GexRequest());
        var second = await service.GetGexTableAsync("aapl", new GexRequest());

        Assert.Same(first, second);
        Assert.Single(_options.Queries);
        Assert.Single(_marketData.QuoteRequests);

        // Different ranges are a different table.
        await service.GetGexTableAsync("AAPL", new GexRequest(Days: 7));
        Assert.Equal(2, _options.Queries.Count);
    }

    [Theory]
    [InlineData("", 30, 10, "symbol")]
    [InlineData("AAPL; DROP", 30, 10, "symbol")]
    [InlineData("AAPL", -1, 10, "days")]
    [InlineData("AAPL", 366, 10, "days")]
    [InlineData("AAPL", 30, 0, "strikeRange")]
    [InlineData("AAPL", 30, 51, "strikeRange")]
    public async Task Invalid_requests_are_rejected_before_any_provider_call(
        string symbol, int days, decimal strikeRange, string field)
    {
        var ex = await Assert.ThrowsAsync<RequestValidationException>(
            () => Service().GetGexTableAsync(symbol, new GexRequest(days, strikeRange)));

        Assert.Contains(field, ex.Errors.Keys);
        Assert.Empty(_marketData.QuoteRequests);
        Assert.Empty(_options.Queries);
    }

    [Theory]
    [InlineData("BRK.B")]
    [InlineData("BF-B")]
    [InlineData("spy")]
    public async Task Share_class_symbols_are_valid(string symbol)
    {
        Assert.Null(await Service().GetGexTableAsync(symbol, new GexRequest()));
    }

    private static OptionContract Contract(decimal strike, string expiration) =>
        new($"AAPL{strike}", OptionSide.Call, strike, DateTimeOffset.Parse($"{expiration}T20:00:00Z"), 1000, 0.3, 0.05, 2m);

    private GexService Service() =>
        new(
            _marketData,
            _options,
            _cache,
            new FixedTimeProvider(Now),
            MicrosoftOptions.Create(new GexOptions()));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
