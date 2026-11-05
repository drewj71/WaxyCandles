namespace WaxyCandles.Api.RateLimiting;

/// <summary>
/// The optional <c>RateLimiting</c> section. Every limit is a fixed window
/// with no queueing; a request past it gets 429.
/// </summary>
public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>
    /// Per user, shared by the endpoints that can spend market data credits on
    /// demand: the GEX board and alert create/update (level alerts resolve a
    /// board). Cached boards still count, so the limit is predictable.
    /// </summary>
    public FixedWindowLimit MarketData { get; set; } = new() { PermitLimit = 30 };

    /// <summary>
    /// Per user, shared by the history import and backtests (which import
    /// missing history first). Every import is a billed provider call.
    /// </summary>
    public FixedWindowLimit HistoryImport { get; set; } = new() { PermitLimit = 5 };

    /// <summary>
    /// Per client IP, for login and register. Behind a reverse proxy every
    /// client has the proxy's IP unless forwarded headers are configured.
    /// </summary>
    public FixedWindowLimit Auth { get; set; } = new() { PermitLimit = 10 };
}

public sealed class FixedWindowLimit
{
    public int PermitLimit { get; set; }

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
}
