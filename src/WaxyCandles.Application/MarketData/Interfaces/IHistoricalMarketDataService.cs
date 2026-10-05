using WaxyCandles.Application.MarketData.Models;
using WaxyCandles.Domain.Enums;

namespace WaxyCandles.Application.MarketData.Interfaces;

public interface IHistoricalMarketDataService
{
    Task ImportHistoricalCandlesAsync(
        string symbol,
        CandleInterval interval,
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