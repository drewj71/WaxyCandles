using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using WaxyCandles.Application.MarketData;
using WaxyCandles.Application.MarketData.Interfaces;
using WaxyCandles.Application.Options.Gex;
using WaxyCandles.Application.Options.Interfaces;
using WaxyCandles.Application.Options.Models;

namespace WaxyCandles.Infrastructure.Options;

public class GexService : IGexService
{
    private readonly IMarketDataProvider _marketDataProvider;
    private readonly IOptionsDataProvider _optionsDataProvider;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _timeProvider;
    private readonly GexOptions _options;

    public GexService(
        IMarketDataProvider marketDataProvider,
        IOptionsDataProvider optionsDataProvider,
        IMemoryCache cache,
        TimeProvider timeProvider,
        IOptions<GexOptions> options)
    {
        _marketDataProvider = marketDataProvider;
        _optionsDataProvider = optionsDataProvider;
        _cache = cache;
        _timeProvider = timeProvider;
        _options = options.Value;
    }

    public async Task<GexTable?> GetGexTableAsync(
        string symbol,
        GexRequest request,
        CancellationToken cancellationToken = default)
    {
        var normalizedSymbol = GexRequestValidator.Validate(symbol, request);

        // Fetch and cache by the ladder window, not the exact request, so the
        // number of distinct (billed) fetches per symbol is bounded however
        // the ranges are varied. Misses are cached too, so repeated loads and
        // refreshes don't each spend credits.
        var window = GexFetchWindow.For(request);

        var fetched = await _cache.GetOrCreateAsync(
            $"gex:{normalizedSymbol}:{window.Days}:{window.StrikeRangePercent}",
            entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = _options.CacheDuration;

                return FetchAsync(normalizedSymbol, window, cancellationToken);
            });

        if (fetched is null)
        {
            return null;
        }

        // Build from the exact requested window, which the fetched one covers.
        var query = Window(normalizedSymbol, fetched.Price, fetched.Today, request);

        var inRange = fetched.Chain with
        {
            Contracts = fetched.Chain.Contracts
                .Where(x =>
                    x.Strike >= query.StrikeMin &&
                    x.Strike <= query.StrikeMax &&
                    MarketHours.TradingDate(x.Expiration) <= query.ExpirationTo)
                .ToList()
        };

        return GexCalculator.Calculate(inRange, _options.RiskFreeRate);
    }

    private async Task<FetchedChain?> FetchAsync(
        string symbol,
        GexRequest window,
        CancellationToken cancellationToken)
    {
        // The current price sets the strike window, so the chain request only
        // returns (and, on per-contract billing, only pays for) strikes near it.
        var quotes = await _marketDataProvider.GetCurrentQuotesAsync(
            [symbol],
            cancellationToken);

        if (quotes.FirstOrDefault() is not { Price: > 0 } quote)
        {
            return null;
        }

        var today = MarketHours.TradingDate(_timeProvider.GetUtcNow());

        var chain = await _optionsDataProvider.GetOptionChainAsync(
            Window(symbol, quote.Price, today, window),
            cancellationToken);

        return chain is null
            ? null
            : new FetchedChain(quote.Price, today, chain);
    }

    private static OptionChainQuery Window(
        string symbol,
        decimal price,
        DateOnly today,
        GexRequest request)
    {
        var range = request.StrikeRangePercent / 100m;

        return new OptionChainQuery(
            symbol,
            today,
            today.AddDays(request.Days),
            Math.Floor(price * (1 - range)),
            Math.Ceiling(price * (1 + range)));
    }

    /// <summary>A fetched chain slice and the price and date that placed its window.</summary>
    private sealed record FetchedChain(decimal Price, DateOnly Today, OptionChain Chain);
}
