using WaxyCandles.Application.MarketData.Models;
using WaxyCandles.Domain.Enums;

namespace WaxyCandles.Application.MarketData.Interfaces;

public interface IHistoricalMarketDataService
{
    Task ImportHistoricalCandlesAsync(
        string symbol,
        CandleInterval interval,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports daily candles for trading dates <paramref name="from"/> to
    /// <paramref name="to"/> (inclusive) in one billed provider request, storing
    /// those not already stored. Returns how many were added.
    /// </summary>
    Task<int> ImportDailyCandlesAsync(
        string symbol,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StockCandleDto>> GetHistoricalCandlesAsync(
        string symbol,
        CandleInterval interval,
        CancellationToken cancellationToken = default);

    Task<StockDetailsDto> GetStockDetailsAsync(
        string symbol,
        CandleInterval interval,
        CancellationToken cancellationToken = default);
}