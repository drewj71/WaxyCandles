using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WaxyCandles.AlertWorker;
using WaxyCandles.Application.Alerts.Interfaces;
using WaxyCandles.Application.MarketData.Messages;
using WaxyCandles.Application.Notifications.Interfaces;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure.Alerts;
using WaxyCandles.Infrastructure.MarketData;
using WaxyCandles.Infrastructure.Messaging;
using WaxyCandles.Infrastructure.Notifications;
using WaxyCandles.Infrastructure.Persistence;
using WaxyCandles.Infrastructure.Telemetry;
using WaxyCandles.OutboxWorker;
using WaxyCandles.Tests.AlertWorker;
using WaxyCandles.Tests.Infrastructure;
using WaxyCandles.Tests.MarketData;
using WaxyCandles.Tests.Notifications;
using WaxyCandles.Tests.Telemetry;
using static WaxyCandles.Tests.Infrastructure.RabbitMqTestBroker;

namespace WaxyCandles.Tests.Pipeline;

/// <summary>
/// The trace context survives every hand-off of the real pipeline (outbox row,
/// RabbitMQ headers, retries, dead-lettering), so one price's journey is one
/// trace. Spans are captured with an in-test listener instead of an exporter.
/// </summary>
public class PipelineTracingTests(IntegrationFixture fixture)
    : DatabaseTestBase(fixture)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    private const string PricePublish = "publish stock-price-updated";
    private const string PriceProcess = "process stock-price-updated";
    private const string AlertPublish = "publish alert-triggered";
    private const string AlertProcess = "process alert-triggered";

    private readonly SpanRecorder _spans = new();
    private readonly RunningWorkers _workers = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly EvaluationProbe _probe = new();
    private readonly FakeMarketDataProvider _provider = new();
    private readonly RecordingNotifier _notifier = new();
    private RabbitMqTestBroker _broker = default!;
    private string _userId = default!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _broker = await ConnectAsync(Fixture);
        await _broker.DeleteTopologyAsync();
        _userId = await SeedUserAsync();
    }

    public override async Task DisposeAsync()
    {
        await _workers.DisposeAsync();
        await _broker.DeleteTopologyAsync();
        await _broker.DisposeAsync();
        _spans.Dispose();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Price_that_fires_an_alert_is_one_trace_from_poll_to_push()
    {
        await SeedAlertAsync("AAPL", AlertDirection.Above, 100m);
        _provider.SetQuote("AAPL", 150m);
        await StartPipelineAsync();

        var traceId = await PollAsync();

        await Eventually.TrueAsync(
            () => Named(traceId, AlertProcess).Count == 1,
            Timeout,
            "the notification consumer's span");

        var storePrice = Single(traceId, "store price");
        var pricePublish = Single(traceId, PricePublish);
        var priceProcess = Single(traceId, PriceProcess);
        var alertPublish = Single(traceId, AlertPublish);
        var alertProcess = Single(traceId, AlertProcess);
        var push = Single(traceId, "signalr push");

        // Each hand-off continues from the span before it.
        Assert.Equal(storePrice.SpanId, pricePublish.ParentSpanId);
        Assert.Equal(pricePublish.SpanId, priceProcess.ParentSpanId);
        Assert.Equal(priceProcess.SpanId, alertPublish.ParentSpanId);
        Assert.Equal(alertPublish.SpanId, alertProcess.ParentSpanId);
        Assert.Equal(alertProcess.SpanId, push.ParentSpanId);

        Assert.Equal("AAPL", storePrice.GetTagItem(WaxyCandlesTelemetry.Symbol));
        Assert.Equal(ActivityKind.Producer, pricePublish.Kind);
        Assert.Equal(ActivityKind.Consumer, priceProcess.Kind);
        Assert.Equal("Processed", priceProcess.GetTagItem(WaxyCandlesTelemetry.Outcome));
        Assert.Contains(priceProcess.Events, e => e.Name == "alert fired");
        Assert.Equal(0, priceProcess.GetTagItem(WaxyCandlesTelemetry.RetryCount));

        // The outbox rows carry the context of the span that wrote them.
        await using var db = NewDbContext();
        var rows = await db.OutboxMessages.ToListAsync();
        Assert.All(rows, row => Assert.Contains(traceId.ToHexString(), row.TraceParent));
    }

    [Fact]
    public async Task Retried_message_stays_in_its_trace()
    {
        _probe.BeforeEvaluate = attempt => attempt == 1
            ? throw new InvalidOperationException("Simulated evaluation failure")
            : Task.CompletedTask;
        await SeedAlertAsync("AAPL", AlertDirection.Above, 100m);
        _provider.SetQuote("AAPL", 150m);
        await StartPipelineAsync();

        var traceId = await PollAsync();

        await Eventually.TrueAsync(
            () => Named(traceId, PriceProcess).Count == 2,
            Timeout,
            "the retried attempt's span");

        var publish = Single(traceId, PricePublish);
        var attempts = Named(traceId, PriceProcess)
            .OrderBy(a => a.GetTagItem(WaxyCandlesTelemetry.RetryCount))
            .ToList();

        // Both attempts hang off the one publish.
        Assert.All(attempts, a => Assert.Equal(publish.SpanId, a.ParentSpanId));

        Assert.Equal(ActivityStatusCode.Error, attempts[0].Status);
        Assert.Equal("retry 1/2 scheduled", attempts[0].GetTagItem(WaxyCandlesTelemetry.Outcome));
        Assert.Contains(attempts[0].Events, e => e.Name == "exception");

        Assert.Equal(1, attempts[1].GetTagItem(WaxyCandlesTelemetry.RetryCount));
        Assert.Equal("Processed", attempts[1].GetTagItem(WaxyCandlesTelemetry.Outcome));
    }

    [Fact]
    public async Task Dead_lettered_message_keeps_its_trace_context()
    {
        _probe.BeforeEvaluate = _ => throw new InvalidOperationException("Always fails");
        await SeedAlertAsync("AAPL", AlertDirection.Above, 100m);
        _provider.SetQuote("AAPL", 150m);
        await StartPipelineAsync();

        var traceId = await PollAsync();

        await Eventually.TrueAsync(
            async () => await _broker.MessageCountAsync(DeadLetterQueue) == 1,
            Timeout,
            "the message to be dead-lettered");

        var deadLettered = await _broker.GetAsync(DeadLetterQueue);
        var context = MessageTracing.FromHeaders(deadLettered!.BasicProperties);
        Assert.Equal(traceId, context.TraceId);

        var last = Named(traceId, PriceProcess)
            .Single(a => Equals(a.GetTagItem(WaxyCandlesTelemetry.RetryCount), 2));
        Assert.Equal("dead-lettered (retries exhausted)", last.GetTagItem(WaxyCandlesTelemetry.Outcome));
    }

    [Fact]
    public async Task Rows_and_messages_without_trace_context_are_processed_as_new_traces()
    {
        // An outbox row written before tracing existed, and a message published
        // without headers (e.g. already queued before the upgrade).
        var fromOutbox = await InsertUntracedRowAsync("AAPL");
        await StartPipelineAsync();

        var fromQueue = new StockPriceUpdatedMessage
        {
            MessageId = Guid.NewGuid(),
            Symbol = "MSFT",
            Price = 300m,
            Timestamp = DateTimeOffset.UtcNow
        };
        await _broker.PublishAsync(fromQueue);

        await Eventually.TrueAsync(
            async () =>
            {
                await using var db = NewDbContext();
                return await db.ProcessedMessages.CountAsync() == 2;
            },
            Timeout,
            "both messages to be processed");

        var publish = _spans.Spans.Single(s =>
            s.DisplayName == PricePublish &&
            Equals(s.GetTagItem(WaxyCandlesTelemetry.MessagingMessageId), fromOutbox.ToString()));
        Assert.Equal(default, publish.ParentSpanId);

        var process = _spans.Spans.Single(s =>
            s.DisplayName == PriceProcess &&
            Equals(s.GetTagItem(WaxyCandlesTelemetry.MessagingMessageId), fromQueue.MessageId.ToString()));
        Assert.Equal(default, process.ParentSpanId);
        Assert.Equal("Processed", process.GetTagItem(WaxyCandlesTelemetry.Outcome));
    }

    [Fact]
    public async Task Malformed_trace_headers_never_affect_processing()
    {
        await StartPipelineAsync();

        var malformed = NewPriceMessage("AAPL");
        await _broker.PublishAsync(Serialize(malformed), new Dictionary<string, object?>
        {
            [MessageTracing.TraceParentHeader] = System.Text.Encoding.UTF8.GetBytes("garbage"),
            [MessageTracing.TraceStateHeader] = new byte[] { 0xFF, 0xFE, 0xFD }
        });

        var wrongType = NewPriceMessage("MSFT");
        await _broker.PublishAsync(Serialize(wrongType), new Dictionary<string, object?>
        {
            [MessageTracing.TraceParentHeader] = 12345L
        });

        await Eventually.TrueAsync(
            async () =>
            {
                await using var db = NewDbContext();
                return await db.ProcessedMessages.CountAsync() == 2;
            },
            Timeout,
            "both messages to be processed");

        foreach (var message in new[] { malformed, wrongType })
        {
            var process = _spans.Spans.Single(s =>
                s.DisplayName == PriceProcess &&
                Equals(s.GetTagItem(WaxyCandlesTelemetry.MessagingMessageId), message.MessageId.ToString()));

            // A new trace, processed first time: no retry, nothing dead-lettered.
            Assert.Equal(default, process.ParentSpanId);
            Assert.Equal("Processed", process.GetTagItem(WaxyCandlesTelemetry.Outcome));
        }

        Assert.Equal(0u, await _broker.MessageCountAsync(DeadLetterQueue));
    }

    // ---- Helpers ----

    private static StockPriceUpdatedMessage NewPriceMessage(string symbol) => new()
    {
        MessageId = Guid.NewGuid(),
        Symbol = symbol,
        Price = 150m,
        Timestamp = DateTimeOffset.UtcNow
    };

    /// <summary>Polls prices inside a root span, as the price polling worker does; returns its trace.</summary>
    private async Task<ActivityTraceId> PollAsync()
    {
        using var root = WaxyCandlesTelemetry.Source.StartActivity("poll prices")!;

        await using var db = NewDbContext();
        await new MarketDataService(db, _provider).UpdatePricesAsync();

        return root.TraceId;
    }

    private List<Activity> Named(ActivityTraceId traceId, string name) =>
        _spans.InTrace(traceId).Where(s => s.DisplayName == name).ToList();

    private Activity Single(ActivityTraceId traceId, string name) =>
        Assert.Single(Named(traceId, name));

    private async Task StartPipelineAsync()
    {
        await _workers.StartAsync<StockPriceConsumer>(Fixture.BuildServices(
            ConnectionString,
            configure: s => s.AddScoped<IAlertEvaluationService>(sp =>
                new ProbingAlertEvaluationService(
                    new AlertEvaluationService(sp.GetRequiredService<AppDbContext>()),
                    _probe)),
            loggerProvider: _logs));

        await Eventually.TrueAsync(
            async () => await _broker.ConsumerCountAsync(MainQueue) >= 1,
            Timeout,
            "consumer to subscribe");

        await _workers.StartAsync<AlertTriggeredConsumer>(Fixture.BuildServices(
            ConnectionString,
            configure: s => s.AddSingleton<IRealtimeNotifier>(_notifier),
            loggerProvider: _logs));

        await Eventually.TrueAsync(
            async () => await _broker.ConsumerCountAsync(QueueTopology.AlertTriggered.Queue) >= 1,
            Timeout,
            "notification consumer to subscribe");

        await _workers.StartAsync<OutboxPublisherWorker>(
            Fixture.BuildServices(ConnectionString, loggerProvider: _logs));
    }

    private async Task<Guid> InsertUntracedRowAsync(string symbol)
    {
        var message = new StockPriceUpdatedMessage
        {
            MessageId = Guid.NewGuid(),
            Symbol = symbol,
            Price = 150m,
            Timestamp = DateTimeOffset.UtcNow
        };

        await using var db = NewDbContext();
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = message.MessageId,
            Type = nameof(StockPriceUpdatedMessage),
            Payload = JsonSerializer.Serialize(message),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        return message.MessageId;
    }

    private async Task SeedAlertAsync(string symbol, AlertDirection direction, decimal target)
    {
        await using var db = NewDbContext();
        db.Alerts.Add(new Alert
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            Symbol = symbol,
            Direction = direction,
            TargetPrice = target,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1)
        });
        await db.SaveChangesAsync();
    }
}
