using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using WaxyCandles.Infrastructure.Health;
using WaxyCandles.Infrastructure.Persistence;

namespace WaxyCandles.Api.Health;

/// <summary>
/// <c>/health/live</c>: the process is up and serving requests (no dependency
/// checks, so a database or broker outage never gets the API restarted).
/// <c>/health/ready</c>: SQL Server (through <see cref="AppDbContext"/>) and the
/// RabbitMQ broker are reachable. Both are anonymous, not rate-limited, and
/// answer 200 when Healthy/Degraded and 503 when Unhealthy, with a JSON body
/// listing each check.
/// </summary>
public static class HealthEndpoints
{
    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";

    public const string DatabaseCheck = "database";
    public const string RabbitMqCheck = "rabbitmq";

    private const string ReadyTag = "ready";

    // Each check is cut off after this, so a probe always gets an answer even
    // when a dependency hangs (SQL Server's own connect timeout is 15 s).
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IServiceCollection AddApiHealthChecks(this IServiceCollection services)
    {
        services
            .AddHealthChecks()
            .AddDbContextCheck<AppDbContext>(
                DatabaseCheck,
                tags: [ReadyTag])
            .AddCheck<RabbitMqBrokerHealthCheck>(
                RabbitMqCheck,
                tags: [ReadyTag],
                timeout: CheckTimeout);

        // AddDbContextCheck has no timeout parameter.
        services.Configure<HealthCheckServiceOptions>(options =>
        {
            foreach (var registration in options.Registrations.Where(x => x.Name == DatabaseCheck))
            {
                registration.Timeout = CheckTimeout;
            }
        });

        return services;
    }

    public static IEndpointRouteBuilder MapApiHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        Map(endpoints, LivePath, _ => false);
        Map(endpoints, ReadyPath, x => x.Tags.Contains(ReadyTag));

        return endpoints;
    }

    private static void Map(
        IEndpointRouteBuilder endpoints,
        string path,
        Func<HealthCheckRegistration, bool> predicate) =>
        endpoints
            .MapHealthChecks(path, new HealthCheckOptions
            {
                Predicate = predicate,
                ResponseWriter = WriteAsync
            })
            .AllowAnonymous()
            .DisableRateLimiting();

    // Descriptions only, never exception messages: the endpoint is anonymous,
    // and HealthCheckService already logs each failure with its exception.
    private static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        context.Response.Headers.CacheControl = "no-store";

        return context.Response.WriteAsync(
            JsonSerializer.Serialize(
                new
                {
                    status = report.Status.ToString(),
                    durationMs = Math.Round(report.TotalDuration.TotalMilliseconds),
                    checks = report.Entries.ToDictionary(
                        x => x.Key,
                        x => new
                        {
                            status = x.Value.Status.ToString(),
                            description = x.Value.Description
                                ?? (x.Value.Exception is { } ex ? ex.GetType().Name : null),
                            durationMs = Math.Round(x.Value.Duration.TotalMilliseconds)
                        })
                },
                Json));
    }
}
