using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WaxyCandles.Application.Backtesting;
using WaxyCandles.Application.MarketData;
using WaxyCandles.Application.MarketData.Interfaces;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure.Persistence;

namespace WaxyCandles.Infrastructure.Backtesting;

/// <summary>
/// Reads the stored daily candles for the range, imports the missing history
/// first if they don't cover it (one provider request at most per backtest,
/// through <see cref="IHistoricalMarketDataService"/>), then runs
/// <see cref="BacktestCalculator"/>. Provider errors propagate, so a timeout
/// or spent credits reach the client as 504/503.
/// </summary>
public class BacktestService : IBacktestService
{
    private readonly AppDbContext _dbContext;
    private readonly IHistoricalMarketDataService _historicalMarketDataService;
    private readonly IMarketCalendar _marketCalendar;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BacktestService> _logger;

    public BacktestService(
        AppDbContext dbContext,
        IHistoricalMarketDataService historicalMarketDataService,
        IMarketCalendar marketCalendar,
        TimeProvider timeProvider,
        ILogger<BacktestService> logger)
    {
        _dbContext = dbContext;
        _historicalMarketDataService = historicalMarketDataService;
        _marketCalendar = marketCalendar;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<BacktestResponse?> RunAsync(
        BacktestRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var backtest = BacktestRequestValidator.Validate(request, MarketHours.TradingDate(now));

        // Today's candle is still forming until the close, so a range reaching
        // the present ends at the last closed session (known exactly from the
        // calendar). An older range ends at its own end date.
        var lastClosed = await MarketHours.LastClosedTradingDateAsync(now, _marketCalendar, cancellationToken);
        var reachesPresent = backtest.To >= lastClosed;
        var end = reachesPresent ? lastClosed : backtest.To;

        var imported = false;

        if (backtest.From <= end &&
            !await IsCoveredAsync(backtest.Symbol, backtest.From, end, reachesPresent, cancellationToken))
        {
            var added = await _historicalMarketDataService.ImportDailyCandlesAsync(
                backtest.Symbol,
                backtest.From,
                end,
                cancellationToken);

            imported = true;

            _logger.LogInformation(
                "Backtest imported {Added} daily candles for {Symbol} ({From} to {To}).",
                added,
                backtest.Symbol,
                backtest.From,
                end);
        }

        var bars = await LoadBarsAsync(backtest.Symbol, backtest.From, end, cancellationToken);

        if (bars.Count < 2)
        {
            return null;
        }

        var result = BacktestCalculator.Calculate(bars, backtest.Strategy, backtest.Costs);

        return new BacktestResponse(
            backtest.Symbol,
            backtest.StrategyType,
            backtest.From,
            backtest.To,
            bars[0].Date,
            bars[^1].Date,
            bars.Count,
            backtest.Strategy.WarmupBars,
            imported,
            backtest.Costs,
            result.Strategy.Metrics,
            result.Benchmark.Metrics,
            result.EquityCurve,
            result.Strategy.Trades,
            bars);
    }

    private async Task<bool> IsCoveredAsync(
        string symbol,
        DateOnly from,
        DateOnly end,
        bool exactEnd,
        CancellationToken cancellationToken)
    {
        var stats = await DailyCandles(symbol, from, end)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                First = g.Min(x => x.Timestamp),
                Last = g.Max(x => x.Timestamp),
                Count = g.Count()
            })
            .FirstOrDefaultAsync(cancellationToken);

        return stats is not null &&
            CandleCoverage.Covers(
                from,
                end,
                exactEnd,
                DateOnly.FromDateTime(stats.First.UtcDateTime),
                DateOnly.FromDateTime(stats.Last.UtcDateTime),
                stats.Count);
    }

    private async Task<List<BacktestBar>> LoadBarsAsync(
        string symbol,
        DateOnly from,
        DateOnly end,
        CancellationToken cancellationToken)
    {
        var candles = await DailyCandles(symbol, from, end)
            .OrderBy(x => x.Timestamp)
            .Select(x => new { x.Timestamp, x.Open, x.High, x.Low, x.Close, x.Volume })
            .ToListAsync(cancellationToken);

        return candles
            .Select(x => new BacktestBar(
                DateOnly.FromDateTime(x.Timestamp.UtcDateTime),
                x.Open,
                x.High,
                x.Low,
                x.Close,
                x.Volume))
            .ToList();
    }

    // Daily candles are stored at UTC midnight of their trading date.
    private IQueryable<Domain.Entities.StockCandle> DailyCandles(
        string symbol,
        DateOnly from,
        DateOnly end)
    {
        var start = UtcMidnight(from);
        var stop = UtcMidnight(end);

        return _dbContext.StockCandles
            .AsNoTracking()
            .Where(x =>
                x.Symbol == symbol &&
                x.Interval == CandleInterval.OneDay &&
                x.Timestamp >= start &&
                x.Timestamp <= stop);
    }

    private static DateTimeOffset UtcMidnight(DateOnly date) =>
        new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}
