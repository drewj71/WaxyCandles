using WaxyCandles.Application.MarketData.Models;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;

namespace WaxyCandles.Application.MarketData.Interfaces;

public interface IMarketDataProvider
{
    Task<MarketQuote?> GetCurrentQuoteAsync(
        string symbol,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StockCandle>> GetHistoricalCandlesAsync(
        string symbol,
        CandleInterval interval,
        CancellationToken cancellationToken = default);
}