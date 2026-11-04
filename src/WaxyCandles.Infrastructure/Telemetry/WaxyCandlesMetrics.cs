using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using WaxyCandles.Application.Alerts.DTOs;

namespace WaxyCandles.Infrastructure.Telemetry;

/// <summary>
/// The app's own metrics, on the <see cref="MeterName"/> meter. Exported over
/// OTLP under the same condition as traces (see
/// <see cref="TelemetryExtensions.AddWaxyCandlesTelemetry"/>); with no listener
/// recording is a no-op.
/// <para>
/// Tags are low-cardinality only: a queue, an outcome, an endpoint kind. Never a
/// symbol, user or message id.
/// </para>
/// <para>
/// Observable gauges never do I/O in their callbacks: they report values the
/// owning loop last stored here (outbox backlog, queue depths, last poll), and
/// report nothing until a value has been stored, so a process that doesn't run
/// that loop doesn't export the gauge.
/// </para>
/// </summary>
public static class WaxyCandlesMetrics
{
    public const string MeterName = "WaxyCandles";

    public static readonly Meter Meter = new(MeterName);

    // Tag names.
    public const string QueueTag = "queue";
    public const string OutcomeTag = "outcome";
    public const string EndpointTag = "endpoint";
    public const string KindTag = "kind";

    // ---- Outbox (OutboxWorker) ----

    public static readonly Counter<long> OutboxPublished = Meter.CreateCounter<long>(
        "waxycandles.outbox.published",
        unit: "{message}",
        description: "Outbox messages published (broker-confirmed), by queue.");

    // ---- Consumers (AlertWorker, API) ----

    public static readonly Counter<long> MessagesConsumed = Meter.CreateCounter<long>(
        "waxycandles.messages.consumed",
        unit: "{message}",
        description: "Deliveries handled by a consumer, by queue and outcome.");

    public static readonly Histogram<double> MessageProcessingDuration = Meter.CreateHistogram<double>(
        "waxycandles.messages.processing.duration",
        unit: "s",
        description: "Time to handle one delivery, including its ack, retry or dead-letter publish.");

    /// <summary>Values of the <see cref="OutcomeTag"/> on <see cref="MessagesConsumed"/>.</summary>
    public static class ConsumeOutcomes
    {
        public const string Processed = "processed";
        public const string AlreadyProcessed = "already_processed";
        public const string Skipped = "skipped";
        public const string RetryScheduled = "retry_scheduled";
        public const string DeadLetteredRetriesExhausted = "dead_lettered_retries_exhausted";
        public const string DeadLetteredUnreadable = "dead_lettered_unreadable";
        public const string Requeued = "requeued";
    }

    // ---- Price polling (PricePollingWorker) ----

    public static readonly Histogram<double> PricePollDuration = Meter.CreateHistogram<double>(
        "waxycandles.price_poll.duration",
        unit: "s",
        description: "Time for one price poll (fetch, store, outbox), by outcome (success/failure).");

    public static readonly Counter<long> PricesStored = Meter.CreateCounter<long>(
        "waxycandles.prices.stored",
        unit: "{price}",
        description: "New prices stored (and queued for publishing). Unchanged quotes aren't counted.");

    // ---- Alerts (AlertWorker) ----

    public static readonly Counter<long> AlertsFired = Meter.CreateCounter<long>(
        "waxycandles.alerts.fired",
        unit: "{alert}",
        description: "Alert fire events committed, by kind (price/level).");

    /// <summary>
    /// Counts the fires of a committed evaluation. Call it only after the
    /// commit, once per committed attempt. Never throws: it runs after the
    /// commit, where an exception would retry a message that has already
    /// been processed.
    /// </summary>
    public static void RecordAlertsFired(AlertEvaluationResult result)
    {
        try
        {
            foreach (var fired in result.Fired)
            {
                AlertsFired.Add(
                    1,
                    new KeyValuePair<string, object?>(
                        KindTag,
                        fired.Level is null ? "price" : "level"));
            }
        }
        catch (Exception)
        {
            // Metrics must never change message handling.
        }
    }

    // ---- Market Data (API, PricePollingWorker) ----

    public static readonly Counter<long> MarketDataRequests = Meter.CreateCounter<long>(
        "waxycandles.marketdata.requests",
        unit: "{request}",
        description: "Market Data (marketdata.app) requests, by endpoint kind and outcome (ok/no_data/error/timeout/cancelled).");

    public static readonly Counter<long> MarketDataCredits = Meter.CreateCounter<long>(
        "waxycandles.marketdata.credits",
        unit: "{credit}",
        description: "Market Data credits consumed, from each response's X-Api-Ratelimit-Consumed header, by endpoint kind.");

    // ---- Gauge state, written by the owning loops ----

    // long.MinValue = not reported yet. Plain longs (not long?) so reads and
    // writes from different threads are atomic.
    private const long Unset = long.MinValue;

    private static OutboxBacklog? _outboxBacklog;
    private static long _marketDataCreditsRemaining = Unset;
    private static readonly ConcurrentDictionary<string, long> QueueDepths = new();

    static WaxyCandlesMetrics()
    {
        Meter.CreateObservableGauge(
            "waxycandles.outbox.pending",
            () => _outboxBacklog is { } b ? [new Measurement<long>(b.Pending)] : Array.Empty<Measurement<long>>(),
            unit: "{message}",
            description: "Routed outbox messages not yet published, as of the outbox worker's last poll.");

        Meter.CreateObservableGauge(
            "waxycandles.outbox.oldest_pending_age",
            () => _outboxBacklog is { } b ? [new Measurement<double>(b.OldestPendingAgeSeconds)] : Array.Empty<Measurement<double>>(),
            unit: "s",
            description: "Age of the oldest routed, unpublished outbox message (0 when none), as of the last poll.");

        Meter.CreateObservableGauge(
            "waxycandles.outbox.unroutable",
            () => _outboxBacklog is { } b ? [new Measurement<long>(b.Unroutable)] : Array.Empty<Measurement<long>>(),
            unit: "{message}",
            description: "Pending outbox messages whose type has no route; they are never published.");

        Meter.CreateObservableGauge(
            "waxycandles.queue.messages",
            () => QueueDepths
                .Select(x => new Measurement<long>(x.Value, new KeyValuePair<string, object?>(QueueTag, x.Key)))
                .ToArray(),
            unit: "{message}",
            description: "Ready messages per queue (main, retry, dead-letter), from the outbox worker's periodic passive declare.");

        Meter.CreateObservableGauge(
            "waxycandles.price_poll.last_success",
            () => PricePollingHeartbeat.LastSuccessfulPollAt is { } t
                ? [new Measurement<long>(t.ToUnixTimeSeconds())]
                : Array.Empty<Measurement<long>>(),
            unit: "s",
            description: "Unix time of the last successful price poll.");

        Meter.CreateObservableGauge(
            "waxycandles.marketdata.credits_remaining",
            () => Interlocked.Read(ref _marketDataCreditsRemaining) is var r and not Unset
                ? [new Measurement<long>(r)]
                : Array.Empty<Measurement<long>>(),
            unit: "{credit}",
            description: "Market Data credits left in the current window, from the last response's X-Api-Ratelimit-Remaining header.");
    }

    public static KeyValuePair<string, object?> Queue(string queue) => new(QueueTag, queue);

    // ---- Outbox backlog ----

    public sealed record OutboxBacklog(long Pending, double OldestPendingAgeSeconds, long Unroutable);

    /// <summary>The backlog as of the outbox worker's last poll, or null before its first.</summary>
    public static OutboxBacklog? CurrentOutboxBacklog => Volatile.Read(ref _outboxBacklog);

    public static void SetOutboxBacklog(long pending, double oldestPendingAgeSeconds, long unroutable) =>
        Volatile.Write(ref _outboxBacklog, new OutboxBacklog(pending, oldestPendingAgeSeconds, unroutable));

    // ---- Queue depth ----

    public static void SetQueueDepth(string queue, long messages) => QueueDepths[queue] = messages;

    /// <summary>The queue is missing or its depth is unknown: stop reporting it rather than report a stale value.</summary>
    public static void ClearQueueDepth(string queue) => QueueDepths.TryRemove(queue, out _);

    public static void ClearQueueDepths() => QueueDepths.Clear();

    public static long? QueueDepth(string queue) =>
        QueueDepths.TryGetValue(queue, out var depth) ? depth : null;

    // ---- Market Data credits ----

    public static long? MarketDataCreditsRemaining =>
        Interlocked.Read(ref _marketDataCreditsRemaining) is var r and not Unset ? r : null;

    public static void SetMarketDataCreditsRemaining(long remaining) =>
        Interlocked.Exchange(ref _marketDataCreditsRemaining, remaining);

    // Elapsed seconds since a Stopwatch timestamp, for the duration histograms.
    public static double SecondsSince(long startTimestamp) =>
        Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;
}
