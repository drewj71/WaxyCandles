using WaxyCandles.Application.Alerts.Messages;
using WaxyCandles.Application.MarketData.Messages;

namespace WaxyCandles.Infrastructure.Messaging;

/// <summary>
/// Which queue each outbox message type is published to, keyed by
/// <c>OutboxMessage.Type</c>.
/// </summary>
public static class MessageRoutes
{
    public static readonly IReadOnlyDictionary<string, QueueTopology> ByMessageType =
        new Dictionary<string, QueueTopology>
        {
            [nameof(StockPriceUpdatedMessage)] = QueueTopology.StockPriceUpdated,
            [nameof(AlertTriggeredMessage)] = QueueTopology.AlertTriggered
        };
}
