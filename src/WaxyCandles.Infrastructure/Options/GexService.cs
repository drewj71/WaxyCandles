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

        // Misses are cached too, so repeated loads and refreshes don't each
        // spend credits.
        return await _cache.GetOrCreateAsync(
            $"gex:{normalizedSymbol}:{request.Days}:{request.StrikeRangePercent}",
            entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = _options.CacheDuration;

                return BuildAsync(normalizedSymbol, request, cancellationToken);
            });
    }

    private async Task<GexTable?> BuildAsync(
        string symbol,
        GexRequest request,
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

        var range = request.StrikeRangePercent / 100m;
        var today = MarketHours.TradingDate(_timeProvider.GetUtcNow());

        var query = new OptionChainQuery(
            symbol,
            today,
            today.AddDays(request.Days),
            Math.Floor(quote.Price * (1 - range)),
            Math.Ceiling(quote.Price * (1 + range)));

        var chain = await _optionsDataProvider.GetOptionChainAsync(
            query,
            cancellationToken);

        if (chain is null)
        {
            return null;
        }

        // Keep to the requested window even if the provider sent more.
        var inRange = chain with
        {
            Contracts = chain.Contracts
                .Where(x =>
                    x.Strike >= query.StrikeMin &&
                    x.Strike <= query.StrikeMax &&
                    MarketHours.TradingDate(x.Expiration) <= query.ExpirationTo)
                .ToList()
        };

        return GexCalculator.Calculate(inRange, _options.RiskFreeRate);
    }
}
