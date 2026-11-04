using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace WaxyCandles.Infrastructure.Resilience;

/// <summary>
/// SQL transient-fault retries (EF Core's SQL Server execution strategy).
/// Read and validated when services are registered, so a bad value stops
/// startup naming the key.
/// </summary>
/// <remarks>
/// Kept short on purpose. Consumers handle one message at a time, so time
/// spent retrying in-process holds up the whole queue; a longer outage should
/// fall through to the queue's own retry and dead-letter path instead. With the
/// defaults the waits are about 0 s, 1 s and 3 s.
/// </remarks>
public sealed record DatabaseRetrySettings(int MaxRetryCount, TimeSpan MaxRetryDelay)
{
    public const string MaxRetryCountKey = "Database:MaxRetryCount";
    public const string MaxRetryDelayKey = "Database:MaxRetryDelay";

    public const int DefaultMaxRetryCount = 3;
    public const int MaxAllowedRetryCount = 10;

    public static readonly TimeSpan DefaultMaxRetryDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxAllowedRetryDelay = TimeSpan.FromSeconds(30);

    public static DatabaseRetrySettings From(IConfiguration configuration)
    {
        var count = SettingReader.ReadInt(configuration, MaxRetryCountKey, DefaultMaxRetryCount);

        if (count is < 0 or > MaxAllowedRetryCount)
        {
            throw new InvalidOperationException(
                $"{MaxRetryCountKey} must be between 0 and {MaxAllowedRetryCount}.");
        }

        var delay = SettingReader.ReadTimeSpan(configuration, MaxRetryDelayKey, DefaultMaxRetryDelay);

        if (delay <= TimeSpan.Zero || delay > MaxAllowedRetryDelay)
        {
            throw new InvalidOperationException(
                $"{MaxRetryDelayKey} must be greater than zero and at most {MaxAllowedRetryDelay.TotalSeconds} seconds.");
        }

        return new DatabaseRetrySettings(count, delay);
    }
}

/// <summary>
/// Per-vendor <see cref="HttpClient.Timeout"/>s. Timeouts only: vendor
/// requests are never retried, because Market Data bills each answered
/// request (option chains per contract) and the poller tries again on its
/// next poll anyway. A 429 is never retried.
/// </summary>
public sealed record HttpClientTimeouts(
    TimeSpan MarketDataApp,
    TimeSpan MarketDataAppOptions,
    TimeSpan AlphaVantage)
{
    public const string MarketDataAppKey = "MarketDataApp:Timeout";
    public const string MarketDataAppOptionsKey = "MarketDataApp:OptionsTimeout";
    public const string AlphaVantageKey = "AlphaVantage:Timeout";

    /// <summary>Prices (one batched call per poll), candles and the market calendar.</summary>
    public static readonly TimeSpan DefaultMarketDataApp = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Option chains (GEX, level alerts) can be several MB, and a timeout on a
    /// response Market Data already billed wastes the credits, so it's generous.
    /// </summary>
    public static readonly TimeSpan DefaultMarketDataAppOptions = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan DefaultAlphaVantage = TimeSpan.FromSeconds(20);

    public static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(2);

    public static HttpClientTimeouts From(IConfiguration configuration) => new(
        Read(configuration, MarketDataAppKey, DefaultMarketDataApp),
        Read(configuration, MarketDataAppOptionsKey, DefaultMarketDataAppOptions),
        Read(configuration, AlphaVantageKey, DefaultAlphaVantage));

    private static TimeSpan Read(IConfiguration configuration, string key, TimeSpan defaultValue)
    {
        var value = SettingReader.ReadTimeSpan(configuration, key, defaultValue);

        if (value <= TimeSpan.Zero || value > MaxTimeout)
        {
            throw new InvalidOperationException(
                $"{key} must be greater than zero and at most {MaxTimeout.TotalSeconds} seconds.");
        }

        return value;
    }
}

internal static class SettingReader
{
    public static int ReadInt(IConfiguration configuration, string key, int defaultValue)
    {
        var raw = configuration[key];

        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidOperationException($"{key} must be a whole number.");
    }

    public static TimeSpan ReadTimeSpan(IConfiguration configuration, string key, TimeSpan defaultValue)
    {
        var raw = configuration[key];

        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        return TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new InvalidOperationException($"{key} must be a time span such as 00:00:05.");
    }
}
