using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using WaxyCandles.Infrastructure.Messaging;

namespace WaxyCandles.Infrastructure.Health;

public static class WorkerHealthExtensions
{
    public const string BackgroundServicesCheck = "background-services";
    public const string OutboxLoopCheck = "outbox-loop";
    public const string ConsumerStallCheck = "consumer-stall";

    /// <summary>
    /// Liveness for a worker (no HTTP server): health checks run every
    /// <c>Health:Period</c> and <see cref="HeartbeatFilePublisher"/> keeps the
    /// heartbeat file fresh while they pass. Includes
    /// <see cref="BackgroundServicesHealthCheck"/>; add worker-specific checks
    /// to the returned builder.
    /// </summary>
    public static IHealthChecksBuilder AddWorkerHealth(
        this IHostApplicationBuilder builder,
        string serviceName)
    {
        builder.Services
            .AddOptions<WorkerHealthOptions>()
            .Bind(builder.Configuration.GetSection(WorkerHealthOptions.SectionName))
            .PostConfigure(o =>
            {
                if (string.IsNullOrWhiteSpace(o.HeartbeatFile))
                {
                    o.HeartbeatFile = WorkerHealthOptions.DefaultHeartbeatFile(serviceName);
                }
            })
            .Validate(
                o => o.Period >= TimeSpan.FromSeconds(1) && o.Period <= TimeSpan.FromMinutes(5),
                "Health:Period must be between 00:00:01 and 00:05:00.")
            .Validate(
                o => o.OutboxLoopMaxAge > TimeSpan.Zero && o.OutboxLoopMaxAge <= TimeSpan.FromDays(1),
                "Health:OutboxLoopMaxAge must be more than zero and at most 1.00:00:00.")
            .Validate(
                o => o.ConsumerMaxProcessingTime > TimeSpan.Zero && o.ConsumerMaxProcessingTime <= TimeSpan.FromDays(1),
                "Health:ConsumerMaxProcessingTime must be more than zero and at most 1.00:00:00.")
            .ValidateOnStart();

        builder.Services
            .AddOptions<HealthCheckPublisherOptions>()
            .Configure<IOptions<WorkerHealthOptions>>((publisher, health) =>
            {
                // An invalid period is reported by the validation above;
                // HealthCheckPublisherOptions' setters would throw a less
                // helpful error first.
                if (health.Value.Period <= TimeSpan.Zero)
                {
                    return;
                }

                // The first heartbeat, shortly after start-up.
                publisher.Delay = health.Value.Period < TimeSpan.FromSeconds(5)
                    ? health.Value.Period
                    : TimeSpan.FromSeconds(5);
                publisher.Period = health.Value.Period;
                publisher.Timeout = TimeSpan.FromSeconds(30);
            });

        builder.Services.AddSingleton<IHealthCheckPublisher, HeartbeatFilePublisher>();

        return builder.Services
            .AddHealthChecks()
            .AddCheck<BackgroundServicesHealthCheck>(BackgroundServicesCheck);
    }

    /// <summary>
    /// The OutboxWorker's loop heartbeat (<see cref="OutboxLoopHeartbeat"/>). The
    /// publish loop beats on every iteration once it has read the outbox
    /// backlog, before and independent of the broker, so the check stays
    /// healthy while RabbitMQ is down but not while the loop is stuck or can't
    /// read the database.
    /// </summary>
    public static IHealthChecksBuilder AddOutboxLoopCheck(this IHealthChecksBuilder builder)
    {
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<OutboxLoopHeartbeat>();

        // A singleton: it counts the start-up allowance from its creation.
        builder.Services.AddSingleton(sp => new OutboxLoopHealthCheck(
            new LoopHeartbeatHealthCheck(
                "outbox publish",
                sp.GetRequiredService<OutboxLoopHeartbeat>(),
                sp.GetRequiredService<IOptions<WorkerHealthOptions>>().Value.OutboxLoopMaxAge,
                sp.GetRequiredService<TimeProvider>())));

        return builder.Add(new HealthCheckRegistration(
            OutboxLoopCheck,
            sp => sp.GetRequiredService<OutboxLoopHealthCheck>().Inner,
            failureStatus: null,
            tags: null));
    }

    /// <summary>
    /// Stall detection for the worker's <see cref="RabbitMqConsumer{TMessage}"/>s:
    /// unhealthy once one delivery has been processing for longer than
    /// <c>Health:ConsumerMaxProcessingTime</c>. Registers the
    /// <see cref="ConsumerProcessingTracker"/> the consumers report to.
    /// </summary>
    public static IHealthChecksBuilder AddConsumerStallCheck(this IHealthChecksBuilder builder)
    {
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<ConsumerProcessingTracker>();

        return builder.Add(new HealthCheckRegistration(
            ConsumerStallCheck,
            sp => new ConsumerStallHealthCheck(
                sp.GetRequiredService<ConsumerProcessingTracker>(),
                sp.GetRequiredService<IOptions<WorkerHealthOptions>>().Value.ConsumerMaxProcessingTime,
                sp.GetRequiredService<TimeProvider>(),
                sp.GetService<IHostApplicationLifetime>()),
            failureStatus: null,
            tags: null));
    }

    // A distinct type, so the singleton can't be confused with another
    // LoopHeartbeatHealthCheck registration.
    private sealed record OutboxLoopHealthCheck(LoopHeartbeatHealthCheck Inner);
}
