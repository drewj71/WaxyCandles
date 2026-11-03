namespace WaxyCandles.Infrastructure.Telemetry;

/// <summary>
/// When the PricePollingWorker last completed a poll. Exported as the
/// <c>waxycandles.price_poll.last_success</c> gauge, and readable from code
/// (e.g. a health check) in the poller's process. Null until the first
/// successful poll since the process started. It isn't updated while the
/// market is closed, so a reader must allow for nights, weekends and holidays.
/// </summary>
public static class PricePollingHeartbeat
{
    // Unix milliseconds; 0 = never. A long so reads and writes are atomic.
    private static long _lastSuccessUnixMs;

    public static DateTimeOffset? LastSuccessfulPollAt =>
        Interlocked.Read(ref _lastSuccessUnixMs) is var ms and not 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : null;

    public static void RecordSuccess(DateTimeOffset at) =>
        Interlocked.Exchange(ref _lastSuccessUnixMs, at.ToUnixTimeMilliseconds());
}
