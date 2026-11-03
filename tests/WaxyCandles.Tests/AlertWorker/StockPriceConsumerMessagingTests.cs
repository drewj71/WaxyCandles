using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using WaxyCandles.AlertWorker;
using WaxyCandles.Application.Alerts.Interfaces;
using WaxyCandles.Application.MarketData.Messages;
using WaxyCandles.Infrastructure.Alerts;
using WaxyCandles.Infrastructure.Messaging;
using WaxyCandles.Infrastructure.Persistence;
using WaxyCandles.Tests.Infrastructure;
using WaxyCandles.Tests.Telemetry;
using static WaxyCandles.Infrastructure.Telemetry.WaxyCandlesMetrics;
using static WaxyCandles.Tests.Infrastructure.RabbitMqTestBroker;

namespace WaxyCandles.Tests.AlertWorker;

/// <summary>
/// The real StockPriceConsumer against RabbitMQ and SQL Server: the
/// waxycandles.messages.consumed counter per outcome, the metadata retry and
/// dead-letter publishes carry, and the prefetch limit.
/// </summary>
public class StockPriceConsumerMessagingTests(IntegrationFixture fixture)
    : DatabaseTestBase(fixture)
{
    private static readonly TimeSpan MessagingTimeout = TimeSpan.FromSeconds(45);

    private const string Consumed = "waxycandles.messages.consumed";
    private const string TraceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

    private readonly RunningWorkers _workers = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly EvaluationProbe _probe = new();
    private readonly MetricRecorder _metrics = new();
    private RabbitMqTestBroker _broker = default!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _broker = await ConnectAsync(Fixture);
        await _broker.DeleteTopologyAsync();
    }

    public override async Task DisposeAsync()
    {
        await _workers.DisposeAsync();
        await _broker.DeleteTopologyAsync();
        await _broker.DisposeAsync();
        _metrics.Dispose();
        await base.DisposeAsync();
    }

    // ---- OPS-1: consumed counter ----

    [Fact]
    public async Task Processed_and_already_processed_deliveries_are_counted_by_queue_and_outcome()
    {
        await StartConsumerAsync(_probe);

        var message = NewMessage();
        await _broker.PublishAsync(message);
        await WaitForConsumedAsync(ConsumeOutcomes.Processed, 1);

        await _broker.PublishAsync(message);
        await WaitForConsumedAsync(ConsumeOutcomes.AlreadyProcessed, 1);

        Assert.Equal(1, ConsumedCount(ConsumeOutcomes.Processed));
        Assert.Equal(0, ConsumedCount(ConsumeOutcomes.RetryScheduled));

        // One duration per delivery, with the same tags.
        Assert.Equal(2, _metrics.Measurements(
            "waxycandles.messages.processing.duration",
            (QueueTag, MainQueue)).Count);
    }

    // ---- MSG-1: retry publish keeps the message's properties ----

    [Fact]
    public async Task Retry_keeps_the_message_properties_and_trace_and_is_counted()
    {
        _probe.BeforeEvaluate = attempt => attempt == 1
            ? throw new InvalidOperationException("Simulated transient failure")
            : Task.CompletedTask;

        await StartConsumerAsync(_probe);

        var message = NewMessage();
        var body = Serialize(message);
        await PublishWithPropertiesAsync(body, message.MessageId);

        // The retry waits 5s in the retry queue; take it from there.
        BasicGetResult? retry = null;
        await Eventually.TrueAsync(
            async () => (retry = await _broker.GetAsync(RetryQueue)) is not null,
            MessagingTimeout,
            "the failed delivery to be published to the retry queue");

        var properties = retry!.BasicProperties;
        Assert.Equal(body, retry.Body.ToArray());
        Assert.True(properties.Persistent);
        Assert.Equal(message.MessageId.ToString(), properties.MessageId);
        Assert.Equal(StockPriceUpdatedMessage.MessageType, properties.Type);
        Assert.Equal("application/json", properties.ContentType);
        Assert.Equal("correlation-1", properties.CorrelationId);
        Assert.Equal(1, RetryHeaders.GetRetryCount(properties));
        Assert.Equal(TraceParent, Header(properties, "traceparent"));

        // A retry is not a dead-letter: no failure headers.
        Assert.False(properties.Headers!.ContainsKey(DeadLetterHeaders.ExceptionType));

        Assert.Equal(1, ConsumedCount(ConsumeOutcomes.RetryScheduled));
    }

    // ---- MSG-1: dead-letter records why and where; OPS-1 counts it ----

    [Fact]
    public async Task Retries_exhausted_dead_letter_records_the_failure_and_is_counted()
    {
        _probe.BeforeEvaluate = _ =>
            throw new InvalidOperationException("Simulated persistent failure");

        await StartConsumerAsync(_probe);

        var message = NewMessage();
        var body = Serialize(message);
        var before = DateTimeOffset.UtcNow;
        await PublishWithPropertiesAsync(body, message.MessageId);

        await Eventually.TrueAsync(
            async () => await _broker.MessageCountAsync(DeadLetterQueue) == 1,
            MessagingTimeout,
            "message to land in the dead-letter queue");

        var deadLettered = await _broker.GetAsync(DeadLetterQueue);
        Assert.NotNull(deadLettered);

        var properties = deadLettered.BasicProperties;
        Assert.Equal(body, deadLettered.Body.ToArray());
        Assert.True(properties.Persistent);
        Assert.Equal(message.MessageId.ToString(), properties.MessageId);
        Assert.Equal(StockPriceUpdatedMessage.MessageType, properties.Type);
        Assert.Equal("application/json", properties.ContentType);
        Assert.Equal("correlation-1", properties.CorrelationId);
        Assert.Equal(TraceParent, Header(properties, "traceparent"));

        Assert.Equal(MainQueue, Header(properties, DeadLetterHeaders.OriginalQueue));
        Assert.Equal(typeof(InvalidOperationException).FullName, Header(properties, DeadLetterHeaders.ExceptionType));
        Assert.Equal("Simulated persistent failure", Header(properties, DeadLetterHeaders.ExceptionMessage));
        Assert.InRange(FailedAt(properties), before, DateTimeOffset.UtcNow);

        // Retries made when dead-lettered: the last attempt was retry 2 of 2.
        Assert.Equal(
            QueueTopology.StockPriceUpdated.MaxRetries.ToString(),
            Header(properties, DeadLetterHeaders.RetriesAttempted));

        // But no live retry count, so a message moved back to the queue gets
        // its full retry budget again.
        Assert.Equal(0, RetryHeaders.GetRetryCount(properties));
        Assert.False(properties.Headers!.ContainsKey(RetryHeaders.RetryCount));

        await WaitForConsumedAsync(ConsumeOutcomes.DeadLetteredRetriesExhausted, 1);
        Assert.Equal(2, ConsumedCount(ConsumeOutcomes.RetryScheduled));
        Assert.Equal(0, ConsumedCount(ConsumeOutcomes.Processed));
    }

    [Theory]
    [InlineData("{ this is not json", "Not valid JSON")]
    [InlineData("null", "The payload is null.")]
    [InlineData("{}", "The message is missing required fields or has invalid values.")]
    public async Task Unreadable_payload_is_dead_lettered_as_unreadable_and_counted(
        string payload,
        string reasonStart)
    {
        await StartConsumerAsync(_probe);

        var body = Encoding.UTF8.GetBytes(payload);
        var messageId = Guid.NewGuid();
        await PublishWithPropertiesAsync(body, messageId);

        await Eventually.TrueAsync(
            async () => await _broker.MessageCountAsync(DeadLetterQueue) == 1,
            MessagingTimeout,
            "unreadable message to be dead-lettered");

        var deadLettered = await _broker.GetAsync(DeadLetterQueue);
        Assert.NotNull(deadLettered);

        var properties = deadLettered.BasicProperties;
        Assert.Equal(body, deadLettered.Body.ToArray());
        Assert.Equal(messageId.ToString(), properties.MessageId);
        Assert.Equal(StockPriceUpdatedMessage.MessageType, properties.Type);
        Assert.Equal(DeadLetterHeaders.UnreadableReason, Header(properties, DeadLetterHeaders.ExceptionType));
        Assert.StartsWith(reasonStart, Header(properties, DeadLetterHeaders.ExceptionMessage));
        Assert.Equal(MainQueue, Header(properties, DeadLetterHeaders.OriginalQueue));
        Assert.Equal("0", Header(properties, DeadLetterHeaders.RetriesAttempted));
        Assert.Equal(0, RetryHeaders.GetRetryCount(properties));
        Assert.Equal(TraceParent, Header(properties, "traceparent"));

        await WaitForConsumedAsync(ConsumeOutcomes.DeadLetteredUnreadable, 1);
        Assert.Empty(_probe.Calls);
    }

    [Fact]
    public async Task Dead_letter_that_cannot_be_published_is_counted_as_requeued_not_dead_lettered()
    {
        await StartConsumerAsync(_probe);
        await _broker.DeleteQueueAsync(DeadLetterQueue);

        await _broker.PublishAsync(Encoding.UTF8.GetBytes("null"));

        // The first attempt's dead-letter publish is unroutable, so it's requeued;
        // the consumer redeclares the queue and the redelivery is dead-lettered.
        await WaitForConsumedAsync(ConsumeOutcomes.DeadLetteredUnreadable, 1);

        Assert.True(ConsumedCount(ConsumeOutcomes.Requeued) >= 1);
        Assert.Equal(1, ConsumedCount(ConsumeOutcomes.DeadLetteredUnreadable));
    }

    // ---- MSG-2: prefetch ----

    [Fact]
    public async Task Prefetch_limit_lets_a_second_consumer_share_a_backlog()
    {
        var first = SlowProbe();
        var second = SlowProbe();

        await StartConsumerAsync(first);
        await AssertBacklogIsSharedAsync(first, second);
    }

    [Fact]
    public async Task Prefetch_limit_is_reapplied_after_connection_recovery()
    {
        var first = SlowProbe();
        var second = SlowProbe();

        await StartConsumerAsync(first);

        // Automatic recovery reopens the channel and re-registers the consumer;
        // without the QoS re-applied it would take the whole backlog again.
        await Fixture.CloseAllRabbitMqConnectionsAsync();

        await Eventually.TrueAsync(
            async () => await TryGetAsync(() => _broker.ConsumerCountAsync(MainQueue)) == 1,
            MessagingTimeout,
            "automatic recovery to re-register the consumer");

        await AssertBacklogIsSharedAsync(first, second);
    }

    /// <summary>
    /// Publishes a backlog while only <paramref name="first"/>'s consumer runs,
    /// then starts a second. Without a prefetch limit the broker would already
    /// have pushed the whole backlog to the first.
    /// </summary>
    private async Task AssertBacklogIsSharedAsync(EvaluationProbe first, EvaluationProbe second)
    {
        const int backlog = 50;

        await using (var connection = await NewConnectionAsync())
        await using (var channel = await connection.CreateChannelAsync())
        {
            for (var i = 0; i < backlog; i++)
            {
                await channel.BasicPublishAsync(
                    exchange: string.Empty,
                    routingKey: MainQueue,
                    mandatory: false,
                    basicProperties: new BasicProperties { Persistent = true },
                    body: Serialize(NewMessage()));
            }
        }

        await Eventually.TrueAsync(
            () => first.Calls.Count >= 1,
            MessagingTimeout,
            "the first consumer to start on the backlog");

        await StartConsumerAsync(second);

        await Eventually.TrueAsync(
            async () =>
            {
                await using var db = NewDbContext();
                return await db.ProcessedMessages.CountAsync() == backlog;
            },
            TimeSpan.FromSeconds(90),
            "the whole backlog to be processed");

        Assert.Equal(backlog, first.Calls.Count + second.Calls.Count);
        Assert.True(
            second.Calls.Count > 0,
            $"The second consumer got none of the backlog (first: {first.Calls.Count}).");

        // At most the prefetch count was pushed to the first before the second
        // subscribed, so the first can't have taken (nearly) all of it.
        Assert.True(first.Calls.Count < backlog);
    }

    private static EvaluationProbe SlowProbe() =>
        new() { BeforeEvaluate = _ => Task.Delay(200) };

    // ---- helpers ----

    private async Task StartConsumerAsync(EvaluationProbe probe)
    {
        var services = Fixture.BuildServices(
            ConnectionString,
            configure: s => s.AddScoped<IAlertEvaluationService>(sp =>
                new ProbingAlertEvaluationService(
                    new AlertEvaluationService(sp.GetRequiredService<AppDbContext>()),
                    probe)),
            loggerProvider: _logs);

        await _workers.StartAsync<StockPriceConsumer>(services);

        var expectedConsumers = _workers.Count;

        await Eventually.TrueAsync(
            async () => await TryGetAsync(() => _broker.ConsumerCountAsync(MainQueue)) >= expectedConsumers,
            MessagingTimeout,
            $"{expectedConsumers} consumer(s) to subscribe to {MainQueue}");
    }

    private double ConsumedCount(string outcome) =>
        _metrics.Sum(Consumed, (QueueTag, MainQueue), (OutcomeTag, outcome));

    private Task WaitForConsumedAsync(string outcome, int count) =>
        Eventually.TrueAsync(
            () => ConsumedCount(outcome) >= count,
            MessagingTimeout,
            $"{count} {outcome} deliveries to be counted");

    private Task<IConnection> NewConnectionAsync() =>
        new ConnectionFactory
        {
            HostName = Fixture.RabbitMqHost,
            Port = Fixture.RabbitMqPort
        }.CreateConnectionAsync();

    /// <summary>Publishes with the properties the outbox sets, plus a correlation id and a trace.</summary>
    private async Task PublishWithPropertiesAsync(byte[] body, Guid messageId)
    {
        await using var connection = await NewConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        await channel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: MainQueue,
            mandatory: false,
            basicProperties: new BasicProperties
            {
                Persistent = true,
                MessageId = messageId.ToString(),
                Type = StockPriceUpdatedMessage.MessageType,
                ContentType = "application/json",
                CorrelationId = "correlation-1",
                Headers = new Dictionary<string, object?>
                {
                    ["traceparent"] = Encoding.UTF8.GetBytes(TraceParent)
                }
            },
            body: body);
    }

    private static string? Header(IReadOnlyBasicProperties properties, string name) =>
        properties.Headers is not null &&
        properties.Headers.TryGetValue(name, out var value) &&
        value is byte[] bytes
            ? Encoding.UTF8.GetString(bytes)
            : null;

    private static DateTimeOffset FailedAt(IReadOnlyBasicProperties properties) =>
        DateTimeOffset.Parse(
            Header(properties, DeadLetterHeaders.FailedAt)!,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private static async Task<uint?> TryGetAsync(Func<Task<uint>> probe)
    {
        try
        {
            return await probe();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static StockPriceUpdatedMessage NewMessage() => new()
    {
        MessageId = Guid.NewGuid(),
        Symbol = "AAPL",
        Price = 150m,
        Timestamp = DateTimeOffset.UtcNow
    };
}
