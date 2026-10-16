using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WaxyCandles.Application.Alerts.Interfaces;
using WaxyCandles.Application.Alerts.Levels;
using WaxyCandles.Application.MarketData;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Infrastructure.Persistence;

namespace WaxyCandles.Infrastructure.Alerts;

/// <summary>
/// Moves each watching GEX level alert's target to where its level is today.
/// A changed target is an edit as far as evaluation is concerned: the alert is
/// re-armed and <see cref="Alert.StateChangedAt"/> stamped, so prices observed
/// before the refresh can't fire it against the new target, and a fire already
/// in flight against the old target fails its unchanged-since check.
/// Each alert is handled at most once per trading date
/// (<see cref="Alert.LevelCheckedOn"/>), so retries and restarts only pick up
/// the alerts whose board failed.
/// </summary>
public class GexLevelAlertRefresher : IGexLevelAlertRefresher
{
    private readonly AppDbContext _dbContext;
    private readonly IGexLevelBoardSource _boards;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GexLevelAlertRefresher> _logger;

    public GexLevelAlertRefresher(
        AppDbContext dbContext,
        IGexLevelBoardSource boards,
        TimeProvider timeProvider,
        ILogger<GexLevelAlertRefresher> logger)
    {
        _dbContext = dbContext;
        _boards = boards;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var today = MarketHours.TradingDate(now);

        // Watching only: a one-time alert that has fired is finished, and a
        // disabled or expired one is re-resolved when it's switched back on.
        var alerts = await _dbContext.Alerts
            .AsNoTracking()
            .Where(AlertQueries.IsWatching(now))
            .Where(a => a.Level != null)
            .Where(a => a.LevelCheckedOn == null || a.LevelCheckedOn != today)
            .ToListAsync(cancellationToken);

        var allLoaded = true;

        // One board per symbol and window, however many alerts use it.
        var boards = alerts.GroupBy(a => (
            a.Symbol,
            Days: a.LevelDays ?? Alert.DefaultLevelDays,
            Range: a.LevelStrikeRange ?? Alert.DefaultLevelStrikeRange));

        foreach (var group in boards)
        {
            GexLevelBoard? board;

            try
            {
                board = await _boards.GetAsync(
                    group.Key.Symbol,
                    group.Key.Days,
                    group.Key.Range,
                    cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Left unchecked for today, so the retry picks these up.
                _logger.LogWarning(
                    ex,
                    "Could not load the {Symbol} GEX board ({Days} days, ±{Range}%). " +
                    "Its {Count} level alert(s) keep their previous target.",
                    group.Key.Symbol,
                    group.Key.Days,
                    group.Key.Range,
                    group.Count());

                allLoaded = false;
                continue;
            }

            if (board is null)
            {
                // No data isn't a transient failure, so not worth retrying today.
                _logger.LogWarning(
                    "No option data for {Symbol}. Its {Count} level alert(s) keep their previous target.",
                    group.Key.Symbol,
                    group.Count());

                foreach (var alert in group)
                {
                    await MarkCheckedAsync(alert, today, levelAsOf: null, cancellationToken);
                }

                continue;
            }

            var stateChangedAt = await StateBoundaryAsync(group.Key.Symbol, now, cancellationToken);

            foreach (var alert in group)
            {
                await RefreshAlertAsync(alert, board, now, today, stateChangedAt, cancellationToken);
            }
        }

        return allLoaded;
    }

    /// <summary>
    /// The time to stamp as the state change. Evaluation compares it with
    /// price observation times, which are the provider's quote times, so it
    /// uses the newest stored price's time rather than the wall clock: the
    /// refresh runs before this poll's prices are written, so every price
    /// already published is at or before it and every later one after it,
    /// even when quotes lag the wall clock.
    /// </summary>
    private async Task<DateTimeOffset> StateBoundaryAsync(
        string symbol,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var latestPrice = await _dbContext.StockPrices
            .Where(p => p.Symbol == symbol)
            .MaxAsync(p => (DateTimeOffset?)p.Timestamp, cancellationToken);

        return latestPrice ?? now;
    }

    private async Task RefreshAlertAsync(
        Alert alert,
        GexLevelBoard board,
        DateTimeOffset now,
        DateOnly today,
        DateTimeOffset stateChangedAt,
        CancellationToken cancellationToken)
    {
        var node = GexLevelTarget.Pick(board.Levels, alert.Level!.Value);

        if (node is null)
        {
            _logger.LogInformation(
                "{Symbol} has no {Level} on today's board. Alert {AlertId} keeps its previous target.",
                alert.Symbol,
                alert.Level,
                alert.Id);

            await MarkCheckedAsync(alert, today, levelAsOf: null, cancellationToken);
            return;
        }

        var (targetPrice, direction) = GexLevelTarget.For(
            node.Strike,
            board.CurrentPrice,
            alert.LevelProximityPercent ?? Alert.DefaultLevelProximityPercent);

        if (targetPrice == alert.TargetPrice &&
            direction == alert.Direction &&
            node.Strike == alert.LevelPrice)
        {
            // Same condition as before: leave its state alone (a fired
            // repeating alert stays fired until the price moves back).
            await MarkCheckedAsync(alert, today, board.AsOf, cancellationToken);
            return;
        }

        // Still watching too: re-arming a one-time alert that fired since it
        // was read would let it fire a second time.
        var updated = await Unchanged(alert)
            .Where(AlertQueries.IsWatching(now))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(a => a.TargetPrice, targetPrice)
                    .SetProperty(a => a.Direction, direction)
                    .SetProperty(a => a.LevelPrice, node.Strike)
                    .SetProperty(a => a.LevelAsOf, board.AsOf)
                    .SetProperty(a => a.LevelCheckedOn, today)
                    .SetProperty(a => a.IsTriggered, false)
                    // Never moved back: a later state change (a fire, or an
                    // edit) already rules out everything before it.
                    .SetProperty(
                        a => a.StateChangedAt,
                        a => a.StateChangedAt != null && a.StateChangedAt > stateChangedAt
                            ? a.StateChangedAt
                            : stateChangedAt),
                cancellationToken);

        if (updated > 0)
        {
            _logger.LogInformation(
                "Alert {AlertId}: {Symbol} {Level} is at {LevelPrice}; target now {Direction} {TargetPrice}.",
                alert.Id,
                alert.Symbol,
                alert.Level,
                node.Strike,
                direction,
                targetPrice);
        }
    }

    /// <summary>Records today's check without touching the target or state.</summary>
    private Task<int> MarkCheckedAsync(
        Alert alert,
        DateOnly today,
        DateTimeOffset? levelAsOf,
        CancellationToken cancellationToken) =>
        Unchanged(alert)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(a => a.LevelCheckedOn, today)
                    .SetProperty(a => a.LevelAsOf, a => levelAsOf ?? a.LevelAsOf),
                cancellationToken);

    /// <summary>
    /// The alert, only if nobody (the user, or another refresh) has changed it
    /// since it was read. A user edit re-resolves the level itself.
    /// </summary>
    private IQueryable<Alert> Unchanged(Alert alert) =>
        _dbContext.Alerts
            .Where(a =>
                a.Id == alert.Id &&
                a.Level == alert.Level &&
                a.LevelProximityPercent == alert.LevelProximityPercent &&
                a.LevelDays == alert.LevelDays &&
                a.LevelStrikeRange == alert.LevelStrikeRange &&
                a.TargetPrice == alert.TargetPrice &&
                a.Direction == alert.Direction);
}
