using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WaxyCandles.Application.Alerts.Interfaces;
using WaxyCandles.Application.Alerts.Messages;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure.Persistence;

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
            var now = DateTimeOffset.UtcNow;

            var alerts = await _dbContext.Alerts
                .AsNoTracking()
                .Where(AlertQueries.IsWatching(now))
                .Where(a => a.Symbol == symbol)
                .ToListAsync(cancellationToken);

            foreach (var alert in alerts)
            {
                var conditionMet = alert.IsConditionMet(price);

                if (!alert.IsTriggered && conditionMet)
                {
                    if (await TryClaimTriggerAsync(alert, now, cancellationToken))
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
                    await RearmAsync(alert, cancellationToken);
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
    /// Fires the alert only if, at the moment of writing, it is still armed,
    /// enabled, unexpired and (for a repeating alert) past its cooldown. The read
    /// above can be stale when price updates for the symbol are evaluated
    /// concurrently or the user just changed the alert; only one of several
    /// concurrent conditional updates can change the row, so only one emits an event.
    /// </summary>
    private async Task<bool> TryClaimTriggerAsync(
        Alert alert,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var claimed = await _dbContext.Alerts
            .Where(a => a.Id == alert.Id && !a.IsTriggered)
            .Where(AlertQueries.IsWatching(now))
            .Where(a =>
                a.Frequency == AlertFrequency.Once ||
                a.TriggeredAt == null ||
                a.TriggeredAt.Value.AddMinutes(a.CooldownMinutes) <= now)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(a => a.IsTriggered, true)
                    .SetProperty(a => a.TriggeredAt, now)
                    .SetProperty(a => a.TriggerCount, a => a.TriggerCount + 1),
                cancellationToken);

        if (claimed == 0)
        {
            return false;
        }

        alert.IsTriggered = true;
        alert.TriggeredAt = now;
        alert.TriggerCount++;
        return true;
    }

    private Task<int> RearmAsync(
        Alert alert,
        CancellationToken cancellationToken) =>
        _dbContext.Alerts
            .Where(a =>
                a.Id == alert.Id &&
                a.IsTriggered &&
                a.Frequency == AlertFrequency.EveryCross)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(a => a.IsTriggered, false),
                cancellationToken);

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
            TriggeredAt = alert.TriggeredAt!.Value
        };

        _dbContext.OutboxMessages.Add(new OutboxMessage
        {
            Id = message.MessageId,
            Type = nameof(AlertTriggeredMessage),
            Payload = JsonSerializer.Serialize(message),
            CreatedAt = message.TriggeredAt
        });
    }
}
