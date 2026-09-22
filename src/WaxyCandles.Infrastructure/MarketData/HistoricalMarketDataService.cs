using Microsoft.EntityFrameworkCore;
using WaxyCandles.Application.MarketData.Interfaces;
using WaxyCandles.Application.MarketData.Models;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure.Persistence;

namespace WaxyCandles.Infrastructure.MarketData;

public class HistoricalMarketDataService
    : IHistoricalMarketDataService
{
    private readonly AppDbContext _dbContext;
    private readonly IMarketDataProvider _marketDataProvider;

    public HistoricalMarketDataService(
        AppDbContext dbContext,
        IMarketDataProvider marketDataProvider)
    {
        _dbContext = dbContext;
        _marketDataProvider = marketDataProvider;
    }

    public async Task ImportHistoricalCandlesAsync(
        string symbol,
        CandleInterval interval,
        CancellationToken cancellationToken = default)
    {
        var candles =
            await _marketDataProvider.GetHistoricalCandlesAsync(
                symbol,
                interval,
                cancellationToken);

        var normalizedSymbol =
            symbol.Trim().ToUpperInvariant();

        var existing = await _dbContext.StockCandles
            .Where(x =>
                x.Symbol == normalizedSymbol &&
                x.Interval == interval)
            .Select(x => x.Timestamp)
            .ToHashSetAsync(cancellationToken);

        foreach (var candle in candles)
        {
            if (existing.Contains(candle.Timestamp))
                continue;

            _dbContext.StockCandles.Add(candle);
        }

        await _dbContext.SaveChangesAsync(
            cancellationToken);
    }

    public async Task<IReadOnlyList<StockCandleDto>> GetHistoricalCandlesAsync(
        string symbol,
        CandleInterval interval,
        CancellationToken cancellationToken = default)
    {
        var normalizedSymbol =
            symbol.Trim().ToUpperInvariant();

        return await _dbContext.StockCandles
            .Where(x =>
                x.Symbol == normalizedSymbol &&
                x.Interval == interval)
            .OrderBy(x => x.Timestamp)
            .Select(x => new StockCandleDto
            {
                Timestamp = x.Timestamp,
                Open = x.Open,
                High = x.High,
                Low = x.Low,
                Close = x.Close,
                Volume = x.Volume
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<StockDetailsDto> GetStockDetailsAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var normalizedSymbol =
            symbol.Trim().ToUpperInvariant();

        var quote =
            await _marketDataProvider.GetCurrentQuoteAsync(
                normalizedSymbol,
                cancellationToken);

        var historicalCandles =
            await GetHistoricalCandlesAsync(
                symbol,
                CandleInterval.OneDay,
                cancellationToken);

        return new StockDetailsDto
        {
            Symbol = normalizedSymbol,
            Quote = quote,
            HistoricalCandles = historicalCandles
        };
    }
}