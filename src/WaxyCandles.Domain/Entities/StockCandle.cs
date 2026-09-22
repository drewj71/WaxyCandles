using WaxyCandles.Domain.Enums;

namespace WaxyCandles.Domain.Entities;

public class StockCandle
{
    public Guid Id { get; set; }

    public string Symbol { get; set; } = default!;

    public DateTimeOffset Timestamp { get; set; }

    public decimal Open { get; set; }

    public decimal High { get; set; }

    public decimal Low { get; set; }

    public decimal Close { get; set; }

    public long Volume { get; set; }
    public CandleInterval Interval { get; set; }
}