using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace WaxyCandles.Api.RateLimiting;

/// <summary>Named policies for <c>[EnableRateLimiting]</c>.</summary>
public static class RateLimitPolicies
{
    public const string MarketData = "market-data";
    public const string HistoryImport = "history-import";
    public const string Auth = "auth";
}

public static class RateLimitingSetup
{
    public static IServiceCollection AddApiRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<RateLimitingOptions>()
            .Bind(configuration.GetSection(RateLimitingOptions.SectionName))
            .Validate(
                o => new[] { o.MarketData, o.HistoryImport, o.Auth }
                    .All(x => x.PermitLimit > 0 && x.Window > TimeSpan.Zero),
                $"Every {RateLimitingOptions.SectionName}:*:PermitLimit and :Window must be more than zero.")
            .ValidateOnStart();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = WriteRejectionAsync;

            options.AddPolicy(
                RateLimitPolicies.MarketData,
                context => FixedWindow(context, PerUser(context), o => o.MarketData));

            options.AddPolicy(
                RateLimitPolicies.HistoryImport,
                context => FixedWindow(context, PerUser(context), o => o.HistoryImport));

            options.AddPolicy(
                RateLimitPolicies.Auth,
                context => FixedWindow(context, PerIp(context), o => o.Auth));
        });

        return services;
    }

    // These endpoints all require sign-in, so the IP fallback only matters if
    // one is ever opened up.
    private static string PerUser(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } userId
            ? $"user:{userId}"
            : PerIp(context);

    // RemoteIpAddress is null in TestServer and for some transports; those
    // share one bucket.
    private static string PerIp(HttpContext context) =>
        $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

    // Settings are read per request (once per new partition), so they follow
    // the app's configuration including test overrides.
    private static RateLimitPartition<string> FixedWindow(
        HttpContext context,
        string partitionKey,
        Func<RateLimitingOptions, FixedWindowLimit> select)
    {
        var limit = select(context.RequestServices
            .GetRequiredService<IOptions<RateLimitingOptions>>()
            .Value);

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit.PermitLimit,
                Window = limit.Window,
                QueueLimit = 0,
                AutoReplenishment = true
            });
    }

    private static async ValueTask WriteRejectionAsync(
        OnRejectedContext context,
        CancellationToken cancellationToken)
    {
        var response = context.HttpContext.Response;

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        }

        // Same { message } shape as ExceptionHandlingMiddleware's errors.
        response.ContentType = "application/json";

        await response.WriteAsync(
            JsonSerializer.Serialize(new { message = "Too many requests. Try again shortly." }),
            cancellationToken);
    }
}
