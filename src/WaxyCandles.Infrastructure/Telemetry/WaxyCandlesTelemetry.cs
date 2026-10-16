using System.Diagnostics;

namespace WaxyCandles.Infrastructure.Telemetry;

/// <summary>
/// The app's own spans. With no listener (tracing not configured)
/// <see cref="ActivitySource.StartActivity(string, ActivityKind)"/> returns
/// null and nothing is recorded.
/// </summary>
public static class WaxyCandlesTelemetry
{
    public const string SourceName = "WaxyCandles";

    public static readonly ActivitySource Source = new(SourceName);

    // Span attribute names: OpenTelemetry messaging conventions where they
    // exist, waxycandles.* for the app's own.
    public const string MessagingSystem = "messaging.system";
    public const string MessagingDestination = "messaging.destination.name";
    public const string MessagingMessageId = "messaging.message.id";
    public const string RetryCount = "waxycandles.retry_count";
    public const string Outcome = "waxycandles.outcome";
    public const string OutboxWaitMs = "waxycandles.outbox.wait_ms";
    public const string Symbol = "waxycandles.symbol";
}
