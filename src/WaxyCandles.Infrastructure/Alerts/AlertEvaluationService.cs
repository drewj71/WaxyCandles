using System.Diagnostics;
using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WaxyCandles.Application.Alerts.DTOs;
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

    public async Task<AlertEvaluationResult> EvaluateAsync(
        string symbol,
        decimal price,
        DateTimeOffset? observedAt = null,
        CancellationToken cancellationToken = default)
    {
        // Claiming a trigger and writing its event must commit together.
        if (_dbContext.Database.CurrentTransaction is not null)
        {
            // The AlertWorker's transaction, which also holds its claim on the
            // message. The caller owns the execution strategy, so a transient
            // error re-runs the caller's whole attempt, this included.
            var fired = await EvaluateOnceAsync(symbol, price, observedAt, added: null, cancellationToken);

            // Not recorded here: nothing is committed yet. The caller records
            // the result after its commit, so a rollback or replay of its
            // attempt never counts a fire.
            return ToResult(fired);
        }

        // No caller transaction: use one here, inside the execution strategy
        // so a transient error re-runs the whole evaluation.
        var addedEvents = new List<OutboxMessage>();

        var committed = await _dbContext.Database.CreateExecutionStrategy().ExecuteAsync(
            async ct =>
            {
                // A failed attempt's events were rolled back but are still
                // tracked. Detach only those: the change tracker may hold the
                // caller's own entities.
                foreach (var stale in addedEvents)
                {
                    _dbContext.Entry(stale).State = EntityState.Detached;
                }

                addedEvents.Clear();

                await using var transaction =
                    await _dbContext.Database.BeginTransactionAsync(ct);

                var fired = await EvaluateOnceAsync(symbol, price, observedAt, addedEvents, ct);

                await transaction.CommitAsync(ct);

                return fired;
            },
            cancellationToken);

        // Only once committed, so a retried attempt isn't counted twice.
        var result = ToResult(committed);
        WaxyCandlesMetrics.RecordAlertsFired(result);
        return result;
    }

    private static AlertEvaluationResult ToResult(List<Alert> fired) =>
        fired.Count == 0
            ? AlertEvaluationResult.None
            : new AlertEvaluationResult(
                fired.Select(a => new FiredAlert(a.Id, a.Level)).ToList());

    /// <summary>
    /// One evaluation, read to save. Safe to run again after a rollback: it
    /// re-reads the alerts, and after an ambiguous commit the conditional
    /// updates no longer match (already fired, or re-armed with a newer
    /// <c>StateChangedAt</c>), so nothing fires twice.
    /// </summary>
    private async Task<List<Alert>> EvaluateOnceAsync(
        string symbol,
        decimal price,
        DateTimeOffset? observedAt,
        List<OutboxMessage>? added,
        CancellationToken cancellationToken)
    {
        // Everything time-based is judged at the price's observation time:
        // expiry, cooldown, and whether this price is newer than the alert's
        // last state change. A price observed before expiry but retried after
        // it still counts.
        var at = observedAt ?? DateTimeOffset.UtcNow;

        var fired = new List<Alert>();

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
                    var outboxMessage = AddTriggeredEvent(alert, price);
                    added?.Add(outboxMessage);
                    fired.Add(alert);
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

        return fired;
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

    private OutboxMessage AddTriggeredEvent(Alert alert, decimal price)
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
            Type = AlertTriggeredMessage.MessageType,
            Payload = JsonSerializer.Serialize(message),
            // Write time, like every outbox row: the outbox publishes in
            // CreatedAt order. TriggeredAt (the price's observation time) stays
            // in the payload.
            CreatedAt = DateTimeOffset.UtcNow
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

        return outboxMessage;
    }
}
