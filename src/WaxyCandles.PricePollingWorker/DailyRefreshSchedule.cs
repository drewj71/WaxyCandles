using WaxyCandles.Application.MarketData;

namespace WaxyCandles.PricePollingWorker;

/// <summary>
/// "Once per US/Eastern trading date, retrying a failure after a delay."
/// Not thread-safe; owned by the polling loop.
/// </summary>
public sealed class DailyRefreshSchedule(TimeSpan retryDelay)
{
    private DateOnly? _doneFor;
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;

    public bool IsDue(DateTimeOffset now) =>
        _doneFor != MarketHours.TradingDate(now) && now >= _nextAttempt;

    public void Succeeded(DateTimeOffset now) =>
        _doneFor = MarketHours.TradingDate(now);

    /// <returns>When the next attempt is due.</returns>
    public DateTimeOffset Failed(DateTimeOffset now) =>
        _nextAttempt = now + retryDelay;
}
