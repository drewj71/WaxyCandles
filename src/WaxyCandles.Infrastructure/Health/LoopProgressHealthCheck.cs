using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace WaxyCandles.Infrastructure.Health;

/// <summary>
/// A loop heartbeat read from outside the loop: <c>marker</c> returns an object
/// the loop replaces on every iteration (compared by reference, so an iteration
/// that stores equal values still counts). Unhealthy once it hasn't changed for
/// longer than <c>maxAge</c>, counted from when this check was created until
/// the first change, so start-up gets the same allowance.
/// <para>
/// Register it as a singleton: it remembers the last marker it saw. Changes are
/// sampled only when the check runs, which is enough because any change between
/// two samples is seen as one.
/// </para>
/// </summary>
public sealed class LoopProgressHealthCheck : IHealthCheck
{
    private readonly string _loopName;
    private readonly Func<object?> _marker;
    private readonly TimeSpan _maxAge;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();

    private object? _lastMarker;
    private DateTimeOffset _lastChangeAt;

    public LoopProgressHealthCheck(
        string loopName,
        Func<object?> marker,
        TimeSpan maxAge,
        TimeProvider timeProvider)
    {
        if (maxAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAge), "Must be more than zero.");
        }

        _loopName = loopName;
        _marker = marker;
        _maxAge = maxAge;
        _timeProvider = timeProvider;
        _lastMarker = marker();
        _lastChangeAt = timeProvider.GetUtcNow();
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        TimeSpan sinceChange;

        lock (_lock)
        {
            var marker = _marker();

            if (!ReferenceEquals(marker, _lastMarker))
            {
                _lastMarker = marker;
                _lastChangeAt = now;
            }

            sinceChange = now - _lastChangeAt;
        }

        return Task.FromResult(sinceChange <= _maxAge
            ? HealthCheckResult.Healthy($"The {_loopName} loop is making progress.")
            : new HealthCheckResult(
                context.Registration.FailureStatus,
                $"The {_loopName} loop hasn't completed an iteration for {sinceChange:g} (limit {_maxAge:g})."));
    }
}
