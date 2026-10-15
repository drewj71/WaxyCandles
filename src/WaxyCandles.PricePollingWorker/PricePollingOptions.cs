namespace WaxyCandles.PricePollingWorker;

/// <summary>"PricePolling" configuration section.</summary>
public class PricePollingOptions
{
    public const string SectionName = "PricePolling";

    /// <summary>
    /// Time between polls. Each poll costs 1 Market Data credit per watched
    /// symbol; Alpha Vantage's free tier (25 requests a day) needs far longer.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Poll only during US regular trading hours (see MarketHours).</summary>
    public bool MarketHoursOnly { get; set; } = true;
}
