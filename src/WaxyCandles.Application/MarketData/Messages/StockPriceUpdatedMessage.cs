namespace WaxyCandles.Application.MarketData.Messages;

public class StockPriceUpdatedMessage
{
    /// <summary>
    /// The <c>OutboxMessage.Type</c> (and AMQP type) this message is routed by.
    /// Fixed string, not nameof: renaming the class must not strand pending rows.
    /// Never change the value.
    /// </summary>
    public const string MessageType = "StockPriceUpdatedMessage";

    public Guid MessageId { get; set; }
    public string Symbol { get; set; } = default!;

    public decimal Price { get; set; }

    public DateTimeOffset Timestamp { get; set; }
}