namespace WaxyCandles.Application.Watchlists.Models;

/// <summary>
/// One row of a watchlist's quote table, built from stored data only (the
/// poller's prices and imported daily candles); never a vendor call.
/// </summary>
public sealed class WatchlistQuoteDto
{
    public string Symbol { get; set; } = default!;

    // Null until the price poller stores a first price for the symbol.
    public decimal? Price { get; set; }
    public decimal? Change { get; set; }
    public decimal? ChangePercent { get; set; }

    /// <summary>Price minus change: the vendor's previous close.</summary>
    public decimal? PreviousClose { get; set; }

    /// <summary>When the latest stored price was observed (provider clock).</summary>
    public DateTimeOffset? PriceTimestamp { get; set; }

    /// <summary>
    /// Lowest and highest stored price of the session the latest price belongs
    /// to (the 12 hours up to it). Polled prices are sampled about once a
    /// minute, so this can be narrower than the exchange's true range.
    /// </summary>
    public decimal? DayLow { get; set; }
    public decimal? DayHigh { get; set; }

    /// <summary>Volume of the latest stored daily candle, and that candle's date.</summary>
    public long? Volume { get; set; }
    public DateTimeOffset? VolumeDate { get; set; }

    /// <summary>
    /// Daily closes for the last 30 days, oldest first, from stored daily
    /// candles. Empty when none are stored.
    /// </summary>
    public IReadOnlyList<SparklinePoint> Sparkline { get; set; } = [];
}

public sealed record SparklinePoint(DateTimeOffset Timestamp, decimal Close);
