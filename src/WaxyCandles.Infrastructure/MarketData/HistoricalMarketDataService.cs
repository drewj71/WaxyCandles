using Microsoft.EntityFrameworkCore;
using WaxyCandles.Application.MarketData.Interfaces;
using WaxyCandles.Application.MarketData.Models;
using WaxyCandles.Domain.Entities;
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

        await StoreNewCandlesAsync(
            symbol.Trim().ToUpperInvariant(),
            interval,
            candles,
            cancellationToken);
    }

    public async Task<int> ImportDailyCandlesAsync(
        string symbol,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        var candles =
            await _marketDataProvider.GetDailyCandlesAsync(
                symbol,
                from,
                to,
                cancellationToken);

        return await StoreNewCandlesAsync(
            symbol.Trim().ToUpperInvariant(),
            CandleInterval.OneDay,
            candles,
            cancellationToken);
    }

    // Two imports of the same symbol can race (e.g. two backtests), and a
    // commit that reported a transient error may have landed before EF's retry
    // inserted the same rows again. Either way the unique (Symbol, Interval,
    // Timestamp) index rejects the save: start again from what is stored now.
    private const int MaxStoreAttempts = 3;

    private async Task<int> StoreNewCandlesAsync(
        string normalizedSymbol,
        CandleInterval interval,
        IReadOnlyList<StockCandle> candles,
        CancellationToken cancellationToken)
    {
        if (candles.Count == 0)
        {
            return 0;
        }

        var first = candles.Min(x => x.Timestamp);
        var last = candles.Max(x => x.Timestamp);

        for (var attempt = 1; ; attempt++)
        {
            var existing = await _dbContext.StockCandles
                .Where(x =>
                    x.Symbol == normalizedSymbol &&
                    x.Interval == interval &&
                    x.Timestamp >= first &&
                    x.Timestamp <= last)
                .Select(x => x.Timestamp)
                .ToHashSetAsync(cancellationToken);

            var added = 0;

            // One row per timestamp, even if the provider repeated one.
            foreach (var candle in candles.DistinctBy(x => x.Timestamp))
            {
                if (existing.Contains(candle.Timestamp))
                    continue;

                candle.Symbol = normalizedSymbol;
                candle.Interval = interval;
                _dbContext.StockCandles.Add(candle);
                added++;
            }

            try
            {
                await _dbContext.SaveChangesAsync(
                    cancellationToken);

                return added;
            }
            catch (DbUpdateException ex) when (
                ex.IsUniqueConstraintViolation() &&
                attempt < MaxStoreAttempts)
            {
                _dbContext.ChangeTracker.Clear();
            }
        }
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
        CandleInterval interval,
        CancellationToken cancellationToken = default)
    {
        var normalizedSymbol =
            symbol.Trim().ToUpperInvariant();

        var latestPrice = await _dbContext.StockPrices
            .Where(x => x.Symbol == normalizedSymbol)
            .OrderByDescending(x => x.Timestamp)
            .FirstOrDefaultAsync(cancellationToken);

        var historicalCandles =
            await GetHistoricalCandlesAsync(
                normalizedSymbol,
                interval,
                cancellationToken);

        var quote = latestPrice is null
            ? null
            : new MarketQuote
            {
                Symbol = latestPrice.Symbol,
                Price = latestPrice.Price,
                // The vendor's change is against the previous close.
                PreviousClose = latestPrice.Price - latestPrice.Change,
                Change = latestPrice.Change,
                ChangePercent = latestPrice.ChangePercent,
                // Lets the web app tell a pushed live price from an older one.
                QuotedAt = latestPrice.Timestamp
            };

        return new StockDetailsDto
        {
            Symbol = normalizedSymbol,
            Quote = quote,
            HistoricalCandles = historicalCandles
        };
    }
}