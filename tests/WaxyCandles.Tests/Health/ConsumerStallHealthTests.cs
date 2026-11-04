using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WaxyCandles.Infrastructure.Health;
using WaxyCandles.Infrastructure.Messaging;

namespace WaxyCandles.Tests.Health;

/// <summary>
/// AlertWorker stall detection and the outbox loop heartbeat's start-up
/// allowance. Telling a stuck consumer from an idle one: only a delivery that
/// stays in processing too long is a stall.
/// </summary>
public class ConsumerStallHealthTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-02T16:00:00Z");

    [Fact]
    public async Task Idle_consumer_is_healthy_however_long_it_waits()
    {
        var time = new MutableTime(Start);
        var tracker = new ConsumerProcessingTracker(time);
        var check = new ConsumerStallHealthCheck(tracker, TimeSpan.FromMinutes(5), time);

        // An idle queue overnight: nothing in flight.
        time.Now += TimeSpan.FromDays(2);
        Assert.Equal(HealthStatus.Healthy, (await RunAsync(check)).Status);

        // A delivery that finishes leaves nothing behind.
        using (tracker.Begin("stock-price-updated"))
        {
            time.Now += TimeSpan.FromMinutes(1);
        }

        time.Now += TimeSpan.FromHours(1);
        Assert.Null(tracker.OldestInFlight());
        Assert.Equal(HealthStatus.Healthy, (await RunAsync(check)).Status);
    }

    [Fact]
    public async Task Delivery_processing_longer_than_the_limit_is_unhealthy_until_it_finishes()
    {
        var time = new MutableTime(Start);
        var tracker = new ConsumerProcessingTracker(time);
        var check = new ConsumerStallHealthCheck(tracker, TimeSpan.FromMinutes(5), time);

        var stuck = tracker.Begin("stock-price-updated");

        time.Now += TimeSpan.FromMinutes(4);

        using (tracker.Begin("other-queue"))
        {
            // Within the limit, e.g. a delivery riding out SQL retries.
            Assert.Equal(HealthStatus.Healthy, (await RunAsync(check)).Status);

            time.Now += TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1);

            // The oldest delivery is the one reported.
            var result = await RunAsync(check);
            Assert.Equal(HealthStatus.Unhealthy, result.Status);
            Assert.Contains("stock-price-updated", result.Description);
        }

        stuck.Dispose();
        Assert.Equal(HealthStatus.Healthy, (await RunAsync(check)).Status);
    }

    [Fact]
    public async Task Stall_check_is_healthy_while_the_host_stops()
    {
        var time = new MutableTime(Start);
        var tracker = new ConsumerProcessingTracker(time);
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();

        var check = new ConsumerStallHealthCheck(
            tracker, TimeSpan.FromMinutes(5), time, new StoppingLifetime(stopping.Token));

        using var _ = tracker.Begin("stock-price-updated");
        time.Now += TimeSpan.FromHours(1);

        Assert.Equal(HealthStatus.Healthy, (await RunAsync(check)).Status);
    }

    [Fact]
    public async Task Loop_heartbeat_that_never_beats_is_unhealthy_after_the_start_up_allowance()
    {
        var time = new MutableTime(Start);
        var check = new LoopHeartbeatHealthCheck(
            "outbox publish", new OutboxLoopHeartbeat(time), TimeSpan.FromMinutes(2), time);

        time.Now += TimeSpan.FromMinutes(2);
        Assert.Equal(HealthStatus.Healthy, (await RunAsync(check)).Status);

        time.Now += TimeSpan.FromSeconds(1);
        var result = await RunAsync(check);
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("outbox publish loop", result.Description);
    }

    [Fact]
    public void Max_processing_time_defaults_to_five_minutes_and_can_be_configured()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), new WorkerHealthOptions().ConsumerMaxProcessingTime);

        using var host = BuildHost("00:10:00");

        Assert.Equal(
            TimeSpan.FromMinutes(10),
            host.Services.GetRequiredService<IOptions<WorkerHealthOptions>>().Value.ConsumerMaxProcessingTime);

        // The consumers report to the tracker the check reads.
        Assert.NotNull(host.Services.GetService<ConsumerProcessingTracker>());
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("1.00:00:01")]
    public void Invalid_max_processing_time_fails_startup_naming_the_key(string value)
    {
        using var host = BuildHost(value);

        var ex = Assert.ThrowsAny<Exception>(() => host.StartAsync().GetAwaiter().GetResult());

        Assert.Contains("Health:ConsumerMaxProcessingTime", ex.Message);
    }

    private static IHost BuildHost(string maxProcessingTime)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Testing",
            DisableDefaults = true
        });

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Health:HeartbeatFile"] =
                Path.Combine(Path.GetTempPath(), $"waxycandles-test-{Guid.NewGuid():N}.healthy"),
            ["Health:ConsumerMaxProcessingTime"] = maxProcessingTime
        });

        builder.Logging.ClearProviders();
        builder.AddWorkerHealth("test-worker").AddConsumerStallCheck();

        return builder.Build();
    }

    private static Task<HealthCheckResult> RunAsync(IHealthCheck check) =>
        check.CheckHealthAsync(new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("test", check, null, null)
        });

    private sealed class StoppingLifetime(CancellationToken stopping) : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class MutableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
