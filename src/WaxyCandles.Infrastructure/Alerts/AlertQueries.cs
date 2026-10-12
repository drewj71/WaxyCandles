using System.Linq.Expressions;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;

namespace WaxyCandles.Infrastructure.Alerts;

public static class AlertQueries
{
    /// <summary>
    /// Alerts that still need prices: enabled, unexpired, and either armed or
    /// repeating (a fired repeating alert needs prices to notice it should re-arm).
    /// Shared by evaluation and the price poller, so they can't disagree.
    /// </summary>
    public static Expression<Func<Alert, bool>> IsWatching(DateTimeOffset now) =>
        a => a.IsEnabled &&
             (a.ExpiresAt == null || a.ExpiresAt > now) &&
             (!a.IsTriggered || a.Frequency == AlertFrequency.EveryCross);
}
