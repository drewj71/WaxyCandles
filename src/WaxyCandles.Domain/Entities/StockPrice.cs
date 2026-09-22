namespace WaxyCandles.Domain.Entities;

public class StockPrice
{
    public Guid Id { get; set; }

    public string Symbol { get; set; } = default!;

    public decimal Price { get; set; }

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}