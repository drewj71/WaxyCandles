using System.Collections.Concurrent;

namespace WaxyCandles.Infrastructure.Messaging;

/// <summary>
/// The deliveries currently inside a consumer's <c>ProcessAsync</c>, and since
/// when, for <see cref="Health.ConsumerStallHealthCheck"/>. Only the processing
/// itself is tracked, not the ack or the retry/dead-letter publish, so waiting
/// on the broker is never mistaken for a stall. An idle consumer has nothing
/// in flight. Register as a singleton; a process without it (the API) tracks
/// nothing.
/// </summary>
public sealed class ConsumerProcessingTracker
{
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<object, InFlightDelivery> _inFlight = new();

    public ConsumerProcessingTracker(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public sealed record InFlightDelivery(string Queue, DateTimeOffset StartedAt);

    /// <summary>Marks a delivery as being processed until the result is disposed. Never throws.</summary>
    public IDisposable Begin(string queue)
    {
        var token = new Token(this);
        _inFlight[token] = new InFlightDelivery(queue, _timeProvider.GetUtcNow());
        return token;
    }

    /// <summary>The delivery that has been processing longest, or null if none is.</summary>
    public InFlightDelivery? OldestInFlight() =>
        _inFlight.Values
            .OrderBy(x => x.StartedAt)
            .FirstOrDefault();

    private sealed class Token(ConsumerProcessingTracker owner) : IDisposable
    {
        public void Dispose() => owner._inFlight.TryRemove(this, out _);
    }
}
