namespace WaxyCandles.Infrastructure.LivePrices;

/// <summary>The optional "LivePrices" configuration section (API only).</summary>
public sealed class LivePricesOptions
{
    public const string SectionName = "LivePrices";

    public static readonly TimeSpan MinPollInterval = TimeSpan.FromMilliseconds(500);

    public static readonly TimeSpan MaxPollInterval = TimeSpan.FromMinutes(1);

    public const int MaxSubscriptionsLimit = 1000;

    /// <summary>
    /// How often the API checks for newly stored prices of the symbols its
    /// clients watch. The poller stores about one a minute per symbol, so this
    /// only bounds how late a push can be. No query runs while nobody watches.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Symbols one browser connection may watch at once.</summary>
    public int MaxSubscriptionsPerConnection { get; set; } = 100;
}
