namespace WaxyCandles.Application.MarketData.Models;

public class StockCandleDto
{
    public DateTimeOffset Timestamp { get; set; }

    public decimal Open { get; set; }

    public decimal High { get; set; }

    public decimal Low { get; set; }

    public decimal Close { get; set; }

    public long Volume { get; set; }
}