using WaxyCandles.Application.Alerts.Messages;
using WaxyCandles.Application.MarketData.Messages;

namespace WaxyCandles.Infrastructure.Messaging;

/// <summary>
/// Which queue each outbox message type is published to, keyed by
/// <c>OutboxMessage.Type</c>.
/// </summary>
public static class MessageRoutes
{
    // Case-insensitive, like the SQL Server collation the outbox filters with,
    // so every row the query selects as routed is also found here.
    public static readonly IReadOnlyDictionary<string, QueueTopology> ByMessageType =
        new Dictionary<string, QueueTopology>(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(StockPriceUpdatedMessage)] = QueueTopology.StockPriceUpdated,
            [nameof(AlertTriggeredMessage)] = QueueTopology.AlertTriggered
        };
}
