using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using WaxyCandles.Infrastructure.Messaging;

namespace WaxyCandles.Infrastructure.Health;

/// <summary>
/// Unhealthy when a delivery has been in a consumer's processing for longer
/// than <see cref="WorkerHealthOptions.ConsumerMaxProcessingTime"/>. Tells a
/// stuck consumer from an idle one: an idle queue (overnight) has nothing in
/// flight. A broker outage doesn't count either: broker waits (ack, retry and
/// dead-letter publishes) aren't tracked, and a delivery whose processing
/// finishes during an outage is cleared as usual.
/// </summary>
public sealed class ConsumerStallHealthCheck(
    ConsumerProcessingTracker tracker,
    TimeSpan maxProcessingTime,
    TimeProvider timeProvider,
    IHostApplicationLifetime? lifetime = null) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (lifetime?.ApplicationStopping.IsCancellationRequested == true)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Host is stopping."));
        }

        var oldest = tracker.OldestInFlight();

        if (oldest is null)
        {
            return Task.FromResult(HealthCheckResult.Healthy("No delivery is being processed."));
        }

        var age = timeProvider.GetUtcNow() - oldest.StartedAt;

        return Task.FromResult(age <= maxProcessingTime
            ? HealthCheckResult.Healthy($"A delivery from {oldest.Queue} has been processing for {age:g}.")
            : new HealthCheckResult(
                context.Registration.FailureStatus,
                $"A delivery from {oldest.Queue} has been processing for {age:g} (limit {maxProcessingTime:g})."));
    }
}
