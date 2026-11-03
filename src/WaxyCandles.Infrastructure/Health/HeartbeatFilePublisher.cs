using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WaxyCandles.Infrastructure.Health;

/// <summary>
/// A worker's liveness signal without an HTTP server: after each run of the
/// health checks it rewrites <see cref="WorkerHealthOptions.HeartbeatFile"/>
/// when the report isn't Unhealthy, and deletes it when it is. A probe (e.g. a
/// container HEALTHCHECK) treats the worker as live while the file exists and
/// was modified within a few <see cref="WorkerHealthOptions.Period"/>s, so a
/// hung process (no more rewrites) and a failing check (file deleted) both read
/// as unhealthy.
/// </summary>
public sealed class HeartbeatFilePublisher(
    IOptions<WorkerHealthOptions> options,
    ILogger<HeartbeatFilePublisher> logger) : IHealthCheckPublisher
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private string? _lastError;

    public string Path => options.Value.HeartbeatFile;

    public async Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
    {
        try
        {
            if (report.Status == HealthStatus.Unhealthy)
            {
                File.Delete(Path);
                return;
            }

            // Written next to the target and moved over it, so a reader never
            // sees a half-written file.
            var temp = Path + ".tmp";
            await File.WriteAllTextAsync(temp, Describe(report), cancellationToken);
            File.Move(temp, Path, overwrite: true);

            _lastError = null;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Logged once per distinct failure, not on every period.
            if (ex.Message != _lastError)
            {
                _lastError = ex.Message;
                logger.LogError(ex, "Could not update the heartbeat file {HeartbeatFile}.", Path);
            }
        }
    }

    // For a person reading the file; probes only look at its age.
    private static string Describe(HealthReport report) =>
        JsonSerializer.Serialize(
            new
            {
                status = report.Status.ToString(),
                at = DateTimeOffset.UtcNow,
                checks = report.Entries.ToDictionary(
                    x => x.Key,
                    x => new { status = x.Value.Status.ToString(), description = x.Value.Description })
            },
            Json);
}
