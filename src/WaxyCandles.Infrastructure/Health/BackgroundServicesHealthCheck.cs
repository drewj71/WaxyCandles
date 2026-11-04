using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace WaxyCandles.Infrastructure.Health;

/// <summary>
/// Unhealthy when any of the host's <see cref="BackgroundService"/>s has stopped
/// running (its loop returned or threw) while the host itself is not stopping.
/// Each worker's loops (consumer reconnect loop, outbox publish loop, poller)
/// are meant to run until shutdown, so a finished one means the process is up
/// but no longer doing its job.
/// </summary>
public sealed class BackgroundServicesHealthCheck(
    IEnumerable<IHostedService> hostedServices,
    IHostApplicationLifetime lifetime) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (lifetime.ApplicationStopping.IsCancellationRequested)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Host is stopping."));
        }

        var stopped = hostedServices
            .OfType<BackgroundService>()
            .Where(x => x.ExecuteTask is { IsCompleted: true })
            .Select(x => x.GetType().Name)
            .ToArray();

        return Task.FromResult(stopped.Length == 0
            ? HealthCheckResult.Healthy("All background services are running.")
            : new HealthCheckResult(
                context.Registration.FailureStatus,
                $"Background service(s) stopped: {string.Join(", ", stopped)}."));
    }
}
