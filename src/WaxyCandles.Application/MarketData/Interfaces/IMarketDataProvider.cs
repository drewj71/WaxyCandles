using WaxyCandles.Application.MarketData.Models;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;

namespace WaxyCandles.Application.MarketData.Interfaces;

public interface IMarketDataProvider
{
    /// <summary>
    /// Current quotes for the given symbols. Symbols the provider has no data
    /// for are left out of the result.
    /// </summary>
    Task<IReadOnlyList<MarketQuote>> GetCurrentQuotesAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StockCandle>> GetHistoricalCandlesAsync(
        string symbol,
        CandleInterval interval,
        CancellationToken cancellationToken = default);
}