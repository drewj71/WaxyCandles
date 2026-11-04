namespace WaxyCandles.Application.MarketData.Exceptions;

/// <summary>Why a market data vendor couldn't answer.</summary>
public enum MarketDataUnavailableReason
{
    /// <summary>The vendor didn't answer within the HttpClient's timeout.</summary>
    Timeout,

    /// <summary>The vendor refused the request for a rate or credit limit (e.g. Market Data's daily credits are spent).</summary>
    RateLimited
}

/// <summary>
/// Thrown at the provider's HTTP boundary when the vendor timed out or rate
/// limited the request. The API maps it to 504 / 503 (ExceptionHandlingMiddleware).
/// The message is fixed and never contains the vendor's URL, token or response body.
/// Never retry on it: vendor requests are billed.
/// </summary>
public sealed class MarketDataUnavailableException : Exception
{
    public MarketDataUnavailableException(
        MarketDataUnavailableReason reason,
        string provider,
        TimeSpan? retryAfter = null,
        Exception? innerException = null)
        : base(MessageFor(reason, provider), innerException)
    {
        Reason = reason;
        Provider = provider;
        RetryAfter = retryAfter is { } delay && delay > TimeSpan.Zero ? delay : null;
    }

    public MarketDataUnavailableReason Reason { get; }

    /// <summary>The vendor's display name, e.g. "Market Data" or "Alpha Vantage".</summary>
    public string Provider { get; }

    /// <summary>How long the vendor asked us to wait, if it said (positive only).</summary>
    public TimeSpan? RetryAfter { get; }

    private static string MessageFor(MarketDataUnavailableReason reason, string provider) =>
        reason switch
        {
            MarketDataUnavailableReason.Timeout => $"{provider} request timed out.",
            MarketDataUnavailableReason.RateLimited => $"{provider} rate limit reached.",
            _ => $"{provider} is unavailable."
        };
}
