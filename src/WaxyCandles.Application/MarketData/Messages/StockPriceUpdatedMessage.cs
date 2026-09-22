namespace WaxyCandles.Application.MarketData.Messages;

public class StockPriceUpdatedMessage
{
    public Guid MessageId { get; set; }
    public string Symbol { get; set; } = default!;

    public decimal Price { get; set; }

    public DateTimeOffset Timestamp { get; set; }
}