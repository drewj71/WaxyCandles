namespace WaxyCandles.Infrastructure.Health;

/// <summary>
/// A heartbeat a background loop records explicitly with <see cref="Beat"/>,
/// read from outside the loop by <see cref="LoopHeartbeatHealthCheck"/>.
/// Thread-safe. Register a subclass per loop as a singleton, so each loop has
/// its own.
/// </summary>
public class LoopHeartbeat
{
    private readonly TimeProvider _timeProvider;

    // UTC ticks; 0 = never. A long so reads and writes are atomic.
    private long _lastBeatTicks;

    public LoopHeartbeat(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    /// <summary>When the loop last beat, or null if it hasn't yet.</summary>
    public DateTimeOffset? LastBeatAt =>
        Interlocked.Read(ref _lastBeatTicks) is var ticks and not 0
            ? new DateTimeOffset(ticks, TimeSpan.Zero)
            : null;

    /// <summary>Records that the loop has made progress, now.</summary>
    public void Beat() =>
        Interlocked.Exchange(ref _lastBeatTicks, _timeProvider.GetUtcNow().UtcTicks);
}

/// <summary>
/// The OutboxWorker's publish loop. It beats once per iteration, after reading
/// the outbox backlog and before (and independent of) the broker.
/// </summary>
public sealed class OutboxLoopHeartbeat(TimeProvider timeProvider) : LoopHeartbeat(timeProvider);
