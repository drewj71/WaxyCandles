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
            var alerts = await _dbContext.Alerts
                .AsNoTracking()
                .Where(a =>
                    !a.IsTriggered &&
                    a.Symbol == symbol)
                .ToListAsync(cancellationToken);

            foreach (var alert in alerts)
            {
                var triggered = alert.Direction switch
                {
                    AlertDirection.Above =>
                        price >= alert.TargetPrice,

                    AlertDirection.Below =>
                        price <= alert.TargetPrice,

                    _ => false
                };

                if (triggered &&
                    await TryClaimTriggerAsync(alert, cancellationToken))
                {
                    AddTriggeredEvent(alert, price);
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
    /// Marks the alert triggered only if it still isn't. The read above can be
    /// stale when two price updates for the symbol are evaluated concurrently
    /// (scaled-out workers): both see the alert untriggered, but only one of
    /// these conditional updates can change the row, so only one emits an event.
    /// </summary>
    private async Task<bool> TryClaimTriggerAsync(
        Alert alert,
        CancellationToken cancellationToken)
    {
        var triggeredAt = DateTimeOffset.UtcNow;

        var claimed = await _dbContext.Alerts
            .Where(a => a.Id == alert.Id && !a.IsTriggered)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(a => a.IsTriggered, true)
                    .SetProperty(a => a.TriggeredAt, triggeredAt),
                cancellationToken);

        if (claimed == 0)
        {
            return false;
        }

        alert.IsTriggered = true;
        alert.TriggeredAt = triggeredAt;
        return true;
    }

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
