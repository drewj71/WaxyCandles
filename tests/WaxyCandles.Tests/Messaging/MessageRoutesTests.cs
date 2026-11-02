using WaxyCandles.Application.Alerts.Messages;
using WaxyCandles.Application.MarketData.Messages;
using WaxyCandles.Infrastructure.Messaging;

namespace WaxyCandles.Tests.Messaging;

public class MessageRoutesTests
{
    [Fact]
    public void Message_type_names_match_the_strings_already_stored_in_outbox_rows()
    {
        // Pending outbox rows and in-flight messages carry these exact strings
        // (scripts/fake-price.ps1 writes one too). Changing a value strands them,
        // since the outbox leaves rows of an unrouted type pending.
        Assert.Equal("StockPriceUpdatedMessage", StockPriceUpdatedMessage.MessageType);
        Assert.Equal("AlertTriggeredMessage", AlertTriggeredMessage.MessageType);
    }

    [Fact]
    public void Routes_contain_exactly_the_known_message_types()
    {
        Assert.Equal(
            new[] { "AlertTriggeredMessage", "StockPriceUpdatedMessage" },
            MessageRoutes.ByMessageType.Keys.Order(StringComparer.Ordinal));

        Assert.Same(QueueTopology.StockPriceUpdated, MessageRoutes.ByMessageType["StockPriceUpdatedMessage"]);
        Assert.Same(QueueTopology.AlertTriggered, MessageRoutes.ByMessageType["AlertTriggeredMessage"]);
    }
}
