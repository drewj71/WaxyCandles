using WaxyCandles.Application.Alerts.Interfaces;
using WaxyCandles.Application.Alerts.Levels;
using WaxyCandles.Application.MarketData.Interfaces;
using WaxyCandles.Application.Options.Gex;
using WaxyCandles.Application.Options.Interfaces;

namespace WaxyCandles.Infrastructure.Alerts;

public class GexLevelBoardSource : IGexLevelBoardSource
{
    private readonly IGexService _gexService;
    private readonly IMarketDataProvider _marketDataProvider;

    public GexLevelBoardSource(
        IGexService gexService,
        IMarketDataProvider marketDataProvider)
    {
        _gexService = gexService;
        _marketDataProvider = marketDataProvider;
    }

    public async Task<GexLevelBoard?> GetAsync(
        string symbol,
        int days,
        decimal strikeRangePercent,
        CancellationToken cancellationToken = default)
    {
        var table = await _gexService.GetGexTableAsync(
            symbol,
            new GexRequest(days, strikeRangePercent),
            cancellationToken);

        if (table is null)
        {
            return null;
        }

        // The chain's underlying price can be the previous session's close
        // (trial plans); which side of a level the price is on needs the
        // current one.
        var quotes = await _marketDataProvider.GetCurrentQuotesAsync(
            [table.Symbol],
            cancellationToken);

        var currentPrice = quotes.FirstOrDefault() is { Price: > 0 } quote
            ? quote.Price
            : table.UnderlyingPrice;

        return new GexLevelBoard(table.GexLevels, table.AsOf, currentPrice);
    }
}
