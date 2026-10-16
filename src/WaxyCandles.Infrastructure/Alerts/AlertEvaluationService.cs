using System.Diagnostics;
using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WaxyCandles.Application.Alerts.Interfaces;
using WaxyCandles.Application.Alerts.Messages;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure.Persistence;
using WaxyCandles.Infrastructure.Telemetry;

namespace WaxyCandles.Infrastructure.Alerts;

public class AlertEvaluationService : IAlertEvaluationService
{
    private readonly AppDbContext _dbContext;

    public AlertEvaluationService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task EvaluateAsync(
    string symbol,
    decimal price,
    DateTimeOffset? observedAt = null,
    CancellationToken cancellationToken = default)
    {
        // Claiming a trigger and writing its event must commit together. The
        // AlertWorker calls this inside its own transaction; otherwise use one here.
        var ownTransaction =
            _dbContext.Database.CurrentTransaction is null
                ? await _dbContext.Database.BeginTransactionAsync(cancellationToken)
                : null;

        try
        {
            // Everything time-based is judged at the price's observation time:
            // expiry, cooldown, and whether this price is newer than the alert's
            // last state change. A price observed before expiry but retried after
            // it still counts.
            var at = observedAt ?? DateTimeOffset.UtcNow;

            var alerts = await _dbContext.Alerts
                .AsNoTracking()
                .Where(AlertQueries.IsWatching(at))
                .Where(a => a.Symbol == symbol)
                .ToListAsync(cancellationToken);

            foreach (var alert in alerts)
            {
                var conditionMet = alert.IsConditionMet(price);

                if (!alert.IsTriggered && conditionMet)
                {
                    if (await TryClaimTriggerAsync(alert, at, cancellationToken))
                    {
                        AddTriggeredEvent(alert, price);
                    }
                }
                else if (alert.IsTriggered &&
                         !conditionMet &&
                         alert.Frequency == AlertFrequency.EveryCross)
                {
                    // The price moved back across the target: arm it for the next
                    // cross. No event; a notification only ever means "it fired".
                    await RearmAsync(alert, at, cancellationToken);
                }
            }

            await _dbContext.SaveChangesAsync(cancellationToken);

            if (ownTransaction is not null)
            {
                await ownTransaction.CommitAsync(cancellationToken);
            }
        }
        finally
        {
            if (ownTransaction is not null)
            {
                await ownTransaction.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// The decision to fire was made on the snapshot read above, which can be
    /// stale: another worker may have fired or re-armed it, or the user may have
    /// edited it. So the update only applies if, at the moment of writing, the
    /// alert is still exactly what was evaluated and still armed and watching,
    /// its cooldown (repeating alerts) has passed, and this price is newer than
    /// its last state change. Only one of several concurrent conditional updates
    /// can change the row, so only one emits an event.
    /// </summary>
    private async Task<bool> TryClaimTriggerAsync(
        Alert alert,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var claimed = await _dbContext.Alerts
            .Where(a => a.Id == alert.Id && !a.IsTriggered)
            .Where(UnchangedSince(alert))
            .Where(PriceIsNewerThanLastChange(at))
            .Where(AlertQueries.IsWatching(at))
            .Where(a =>
                a.Frequency == AlertFrequency.Once ||
                a.TriggeredAt == null ||
                a.TriggeredAt.Value.AddMinutes(a.CooldownMinutes) <= at)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(a => a.IsTriggered, true)
                    .SetProperty(a => a.TriggeredAt, at)
                    .SetProperty(a => a.TriggerCount, a => a.TriggerCount + 1)
                    .SetProperty(a => a.StateChangedAt, at),
                cancellationToken);

        if (claimed == 0)
        {
            return false;
        }

        alert.IsTriggered = true;
        alert.TriggeredAt = at;
        alert.TriggerCount++;
        alert.StateChangedAt = at;
        return true;
    }

    /// <summary>
    /// Same safeguards as firing: re-arm only the alert that was evaluated, and
    /// only with a price newer than the fire. Otherwise a delayed older price
    /// (e.g. 99 observed before the 101 that fired it) would re-arm it while the
    /// price is still above the target, and the next poll would fire it again.
    /// </summary>
    private Task<int> RearmAsync(
        Alert alert,
        DateTimeOffset at,
        CancellationToken cancellationToken) =>
        _dbContext.Alerts
            .Where(a => a.Id == alert.Id && a.IsTriggered)
            .Where(UnchangedSince(alert))
            .Where(PriceIsNewerThanLastChange(at))
            .Where(a => a.Frequency == AlertFrequency.EveryCross)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(a => a.IsTriggered, false)
                    .SetProperty(a => a.StateChangedAt, at),
                cancellationToken);

    // What the fire/re-arm decision was based on. The event reports these
    // values too, so they must still be the alert's. A level alert's daily
    // refresh changes them just as an edit does.
    private static Expression<Func<Alert, bool>> UnchangedSince(Alert snapshot)
    {
        var targetPrice = snapshot.TargetPrice;
        var direction = snapshot.Direction;
        var frequency = snapshot.Frequency;
        var level = snapshot.Level;
        var levelPrice = snapshot.LevelPrice;

        return a =>
            a.TargetPrice == targetPrice &&
            a.Direction == direction &&
            a.Frequency == frequency &&
            a.Level == level &&
            a.LevelPrice == levelPrice;
    }

    private static Expression<Func<Alert, bool>> PriceIsNewerThanLastChange(
        DateTimeOffset at) =>
        a => a.StateChangedAt == null || a.StateChangedAt < at;

    private void AddTriggeredEvent(Alert alert, decimal price)
    {
        var message = new AlertTriggeredMessage
        {
            MessageId = Guid.NewGuid(),
            AlertId = alert.Id,
            UserId = alert.UserId,
            Symbol = alert.Symbol,
            Direction = alert.Direction,
            TargetPrice = alert.TargetPrice,
            TriggerPrice = price,
            TriggeredAt = alert.TriggeredAt!.Value,
            Level = alert.Level,
            LevelPrice = alert.LevelPrice
        };

        var outboxMessage = new OutboxMessage
        {
            Id = message.MessageId,
            Type = nameof(AlertTriggeredMessage),
            Payload = JsonSerializer.Serialize(message),
            CreatedAt = message.TriggeredAt
        };

        // Evaluated inside the price message's "process" span, so the
        // notification joins the trace of the price that fired it.
        MessageTracing.Stamp(outboxMessage);
        Activity.Current?.AddEvent(new ActivityEvent(
            "alert fired",
            tags: new ActivityTagsCollection
            {
                ["waxycandles.alert_id"] = alert.Id.ToString(),
                [WaxyCandlesTelemetry.Symbol] = alert.Symbol
            }));

        _dbContext.OutboxMessages.Add(outboxMessage);
    }
}
