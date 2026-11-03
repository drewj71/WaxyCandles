using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WaxyCandles.Infrastructure.Health;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Health;

/// <summary>
/// Worker liveness: the checks, and the heartbeat file a container probe reads,
/// in a real generic host like the workers' Program.cs builds.
/// </summary>
public class WorkerHealthTests : IDisposable
{
    private readonly string _heartbeatFile =
        Path.Combine(Path.GetTempPath(), $"waxycandles-test-{Guid.NewGuid():N}.healthy");

    public void Dispose()
    {
        File.Delete(_heartbeatFile);
        File.Delete(_heartbeatFile + ".tmp");
    }

    [Fact]
    public async Task Heartbeat_file_is_kept_fresh_while_the_background_services_run()
    {
        using var host = BuildHost(services => services.AddHostedService<RunsUntilStopped>());

        await host.StartAsync();

        await Eventually.TrueAsync(() => File.Exists(_heartbeatFile), TimeSpan.FromSeconds(15), "the first heartbeat");
        var firstWrite = File.GetLastWriteTimeUtc(_heartbeatFile);

        await Eventually.TrueAsync(
            () => File.Exists(_heartbeatFile) && File.GetLastWriteTimeUtc(_heartbeatFile) > firstWrite,
            TimeSpan.FromSeconds(15),
            "a later heartbeat");

        Assert.Contains("\"Healthy\"", await File.ReadAllTextAsync(_heartbeatFile));

        await host.StopAsync();
    }

    [Fact]
    public async Task Heartbeat_file_is_removed_once_a_background_service_has_stopped()
    {
        var stop = new TaskCompletionSource();

        using var host = BuildHost(services =>
        {
            services.AddSingleton(stop);
            services.AddHostedService<RunsUntilStopped>();
            services.AddHostedService<FinishesWhenTold>();
        });

        await host.StartAsync();
        await Eventually.TrueAsync(() => File.Exists(_heartbeatFile), TimeSpan.FromSeconds(15), "the first heartbeat");

        stop.SetResult();

        await Eventually.TrueAsync(() => !File.Exists(_heartbeatFile), TimeSpan.FromSeconds(15), "the heartbeat file to be removed");

        await host.StopAsync();
    }

    [Fact]
    public async Task Background_services_check_names_a_stopped_service()
    {
        var stop = new TaskCompletionSource();
        stop.SetResult();

        var finished = new FinishesWhenTold(stop);
        await finished.StartAsync(CancellationToken.None);
        await finished.ExecuteTask!;

        var check = new BackgroundServicesHealthCheck(
            [finished, new RunsUntilStopped()],
            new FakeLifetime());

        var result = await RunAsync(check);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains(nameof(FinishesWhenTold), result.Description);
    }

    [Fact]
    public async Task Loop_progress_is_unhealthy_only_after_the_marker_stops_changing()
    {
        var time = new MutableTime(DateTimeOffset.Parse("2026-09-02T16:00:00Z"));
        object? marker = null;

        var check = new LoopProgressHealthCheck("test", () => marker, TimeSpan.FromMinutes(2), time);

        // Start-up allowance before the loop's first iteration.
        time.Now += TimeSpan.FromSeconds(90);
        Assert.Equal(HealthStatus.Healthy, (await RunAsync(check)).Status);

        // An iteration (a new object, even with the same values) resets the clock.
        time.Now += TimeSpan.FromSeconds(60);
        marker = new object();
        Assert.Equal(HealthStatus.Healthy, (await RunAsync(check)).Status);

        time.Now += TimeSpan.FromSeconds(110);
        Assert.Equal(HealthStatus.Healthy, (await RunAsync(check)).Status);

        time.Now += TimeSpan.FromSeconds(20);
        var stale = await RunAsync(check);
        Assert.Equal(HealthStatus.Unhealthy, stale.Status);
        Assert.Contains("test loop", stale.Description);
    }

    [Fact]
    public void Default_heartbeat_file_is_per_service_in_the_temp_directory()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Testing",
            DisableDefaults = true
        });

        builder.AddWorkerHealth("alert-worker");
        using var host = builder.Build();

        var publisher = host.Services.GetServices<IHealthCheckPublisher>()
            .OfType<HeartbeatFilePublisher>()
            .Single();

        Assert.Equal(Path.Combine(Path.GetTempPath(), "waxycandles-alert-worker.healthy"), publisher.Path);
    }

    [Fact]
    public void Invalid_period_fails_startup()
    {
        using var host = BuildHost(_ => { }, period: "00:00:00");

        var ex = Assert.ThrowsAny<Exception>(() => host.StartAsync().GetAwaiter().GetResult());

        Assert.Contains("Health:Period", ex.Message);
    }

    private IHost BuildHost(Action<IServiceCollection> configure, string period = "00:00:01")
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Testing",
            DisableDefaults = true
        });

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Health:HeartbeatFile"] = _heartbeatFile,
            ["Health:Period"] = period
        });

        // A finished background service must not stop the host here: the
        // check is what's under test.
        builder.Services.Configure<HostOptions>(o =>
            o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

        builder.Logging.ClearProviders();
        builder.AddWorkerHealth("test-worker");
        configure(builder.Services);

        return builder.Build();
    }

    private static Task<HealthCheckResult> RunAsync(IHealthCheck check) =>
        check.CheckHealthAsync(new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("test", check, null, null)
        });

    private sealed class RunsUntilStopped : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
            Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private sealed class FinishesWhenTold(TaskCompletionSource stop) : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => stop.Task;
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class MutableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
