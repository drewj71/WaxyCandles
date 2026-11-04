using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace WaxyCandles.Infrastructure.Health;

/// <summary>
/// Unhealthy once a <see cref="LoopHeartbeat"/> hasn't beaten for longer than
/// <c>maxAge</c>. Before the first beat the age is counted from when this check
/// was created, so start-up gets the same allowance.
/// </summary>
public sealed class LoopHeartbeatHealthCheck : IHealthCheck
{
    private readonly string _loopName;
    private readonly LoopHeartbeat _heartbeat;
    private readonly TimeSpan _maxAge;
    private readonly TimeProvider _timeProvider;
    private readonly DateTimeOffset _createdAt;

    public LoopHeartbeatHealthCheck(
        string loopName,
        LoopHeartbeat heartbeat,
        TimeSpan maxAge,
        TimeProvider timeProvider)
    {
        if (maxAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAge), "Must be more than zero.");
        }

        _loopName = loopName;
        _heartbeat = heartbeat;
        _maxAge = maxAge;
        _timeProvider = timeProvider;
        _createdAt = timeProvider.GetUtcNow();
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var lastBeat = _heartbeat.LastBeatAt ?? _createdAt;
        var sinceBeat = now - lastBeat;

        return Task.FromResult(sinceBeat <= _maxAge
            ? HealthCheckResult.Healthy($"The {_loopName} loop is making progress.")
            : new HealthCheckResult(
                context.Registration.FailureStatus,
                $"The {_loopName} loop hasn't completed an iteration for {sinceBeat:g} (limit {_maxAge:g})."));
    }
}
