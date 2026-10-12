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
        var alerts = await _dbContext.Alerts
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

            if (triggered)
            {
                alert.IsTriggered = true;
                alert.TriggeredAt = DateTimeOffset.UtcNow;

                AddTriggeredEvent(alert, price);
            }
        }

        // One SaveChanges for the alert updates and their outbox events, so an
        // event exists exactly when its alert is marked triggered. In the
        // AlertWorker this also runs inside the consumer's transaction.
        await _dbContext.SaveChangesAsync(cancellationToken);
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
