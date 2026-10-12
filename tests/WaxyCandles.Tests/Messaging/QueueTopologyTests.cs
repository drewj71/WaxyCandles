using WaxyCandles.Application.Alerts.Messages;
using WaxyCandles.Application.MarketData.Messages;
using WaxyCandles.Infrastructure.Messaging;

namespace WaxyCandles.Tests.Messaging;

public class QueueTopologyTests
{
    [Fact]
    public void Stock_price_retry_settings_are_unchanged()
    {
        // These become arguments of queues that already exist on deployed brokers.
        // RabbitMQ rejects a redeclaration with different arguments (406), so
        // changing them needs a migration plan, not just an edit.
        var topology = QueueTopology.StockPriceUpdated;

        Assert.Equal(2, topology.MaxRetries);
        Assert.Equal(TimeSpan.FromSeconds(5), topology.RetryDelay);
        Assert.Equal("stock-price-retry", topology.RetryQueue);
    }

    [Fact]
    public void Alert_events_get_a_longer_retry_window_than_price_updates()
    {
        // A dead-lettered alert event is a notification the user never receives,
        // so it should survive a short database outage.
        var alert = QueueTopology.AlertTriggered;
        var window = alert.RetryDelay * alert.MaxRetries;

        Assert.True(window >= TimeSpan.FromMinutes(2), $"Retry window is only {window}.");
        Assert.True(window > QueueTopology.StockPriceUpdated.RetryDelay * QueueTopology.StockPriceUpdated.MaxRetries);
    }

    [Fact]
    public void Every_message_type_is_routed_to_its_own_queue()
    {
        Assert.Same(
            QueueTopology.StockPriceUpdated,
            MessageRoutes.ByMessageType[nameof(StockPriceUpdatedMessage)]);

        Assert.Same(
            QueueTopology.AlertTriggered,
            MessageRoutes.ByMessageType[nameof(AlertTriggeredMessage)]);
    }
}
