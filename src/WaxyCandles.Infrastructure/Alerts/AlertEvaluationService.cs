using Microsoft.EntityFrameworkCore;
using WaxyCandles.Application.Alerts.Interfaces;
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
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}