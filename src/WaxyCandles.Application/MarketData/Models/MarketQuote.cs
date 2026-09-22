namespace WaxyCandles.Application.MarketData.Models;

public class MarketQuote
{
    public string Symbol { get; set; } = default!;

    public decimal Price { get; set; }

    public decimal PreviousClose { get; set; }

    public decimal Change { get; set; }

    public decimal ChangePercent { get; set; }

    public long Volume { get; set; }

    public DateTimeOffset? LatestTradingDay { get; set; }
}