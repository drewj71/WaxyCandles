using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace WaxyCandles.Infrastructure.Telemetry;

public static class TelemetryExtensions
{
    /// <summary>The standard OpenTelemetry setting for where to send traces and logs.</summary>
    public const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>
    /// Exports traces (the app's spans, outgoing HTTP, SQL) and logs over OTLP,
    /// when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set. Unset means tracing is
    /// off: no listener, so no spans are created and nothing changes.
    /// Sampling follows the standard <c>OTEL_TRACES_SAMPLER</c> settings
    /// (default: every trace).
    /// </summary>
    public static IHostApplicationBuilder AddWaxyCandlesTelemetry(
        this IHostApplicationBuilder builder,
        string serviceName,
        Action<TracerProviderBuilder>? configureTracing = null)
    {
        if (string.IsNullOrWhiteSpace(builder.Configuration[OtlpEndpointKey]))
        {
            return builder;
        }

        // Log records carry the trace and span IDs of the span they were
        // written in, so the dashboard shows each span's logs.
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing =>
            {
                // Deliberately not RabbitMQ.Client's own sources
                // ("RabbitMQ.Client.*"): when listened to, the client injects
                // its own traceparent on publish, overwriting the one the
                // outbox carries across and the one copied onto retries, so
                // retries would no longer hang off the original publish.
                tracing
                    .AddSource(WaxyCandlesTelemetry.SourceName)
                    // Market Data / Alpha Vantage calls. Request headers
                    // (including the bearer token) aren't recorded.
                    .AddHttpClientInstrumentation()
                    // Statement text only, never parameter values.
                    .AddSqlClientInstrumentation();

                configureTracing?.Invoke(tracing);
            })
            .UseOtlpExporter();

        return builder;
    }
}
