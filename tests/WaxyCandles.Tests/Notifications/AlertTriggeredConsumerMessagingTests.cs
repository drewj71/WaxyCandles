using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using WaxyCandles.Application.Alerts.Messages;
using WaxyCandles.Application.Notifications.Interfaces;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure.Messaging;
using WaxyCandles.Infrastructure.Notifications;
using WaxyCandles.Tests.Infrastructure;
using WaxyCandles.Tests.Telemetry;
using static WaxyCandles.Infrastructure.Telemetry.WaxyCandlesMetrics;

namespace WaxyCandles.Tests.Notifications;

/// <summary>
/// The consumed counter and dead-letter metadata for the alert-triggered queue.
/// The retry path (5 retries 30s apart) is shared with StockPriceConsumer and
/// covered there.
/// </summary>
public class AlertTriggeredConsumerMessagingTests(IntegrationFixture fixture)
    : DatabaseTestBase(fixture)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly QueueTopology Topology = QueueTopology.AlertTriggered;

    private const string Consumed = "waxycandles.messages.consumed";

    private readonly RunningWorkers _workers = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly RecordingNotifier _notifier = new();
    private readonly MetricRecorder _metrics = new();
    private RabbitMqTestBroker _broker = default!;
    private string _userId = default!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _broker = await RabbitMqTestBroker.ConnectAsync(Fixture);
        await _broker.DeleteTopologyAsync();
        _userId = await SeedUserAsync();
    }

    public override async Task DisposeAsync()
    {
        await _workers.DisposeAsync();
        await _broker.DeleteTopologyAsync();
        await _broker.DisposeAsync();
        _metrics.Dispose();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Processed_already_processed_and_skipped_events_are_counted()
    {
        await StartConsumerAsync();

        var message = NewMessage(_userId);
        await PublishAsync(message);
        await WaitForConsumedAsync(ConsumeOutcomes.Processed, 1);

        await PublishAsync(message);
        await WaitForConsumedAsync(ConsumeOutcomes.AlreadyProcessed, 1);

        // The recipient no longer exists.
        await PublishAsync(NewMessage("deleted-user"));
        await WaitForConsumedAsync(ConsumeOutcomes.Skipped, 1);

        Assert.Equal(1, ConsumedCount(ConsumeOutcomes.Processed));
        Assert.Equal(0, ConsumedCount(ConsumeOutcomes.DeadLetteredUnreadable));
    }

    [Fact]
    public async Task Invalid_event_is_dead_lettered_with_its_origin_and_reason_and_counted()
    {
        await StartConsumerAsync();

        var invalid = NewMessage(userId: "");
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(invalid));

        await using (var connection = await new ConnectionFactory
                     {
                         HostName = Fixture.RabbitMqHost,
                         Port = Fixture.RabbitMqPort
                     }.CreateConnectionAsync())
        await using (var channel = await connection.CreateChannelAsync())
        {
            await channel.BasicPublishAsync(
                exchange: string.Empty,
                routingKey: Topology.Queue,
                mandatory: false,
                basicProperties: new BasicProperties
                {
                    Persistent = true,
                    MessageId = invalid.MessageId.ToString(),
                    Type = AlertTriggeredMessage.MessageType,
                    ContentType = "application/json"
                },
                body: body);
        }

        await Eventually.TrueAsync(
            async () => await _broker.MessageCountAsync(Topology.DeadLetterQueue) == 1,
            Timeout,
            "invalid event to be dead-lettered");

        var deadLettered = await _broker.GetAsync(Topology.DeadLetterQueue);
        Assert.NotNull(deadLettered);

        var properties = deadLettered.BasicProperties;
        Assert.Equal(body, deadLettered.Body.ToArray());
        Assert.Equal(invalid.MessageId.ToString(), properties.MessageId);
        Assert.Equal(AlertTriggeredMessage.MessageType, properties.Type);
        Assert.Equal("application/json", properties.ContentType);
        Assert.Equal(Topology.Queue, Header(properties, DeadLetterHeaders.OriginalQueue));
        Assert.Equal(DeadLetterHeaders.UnreadableReason, Header(properties, DeadLetterHeaders.ExceptionType));
        Assert.NotNull(Header(properties, DeadLetterHeaders.FailedAt));
        Assert.Equal("0", Header(properties, DeadLetterHeaders.RetriesAttempted));
        Assert.Equal(0, RetryHeaders.GetRetryCount(properties));

        await WaitForConsumedAsync(ConsumeOutcomes.DeadLetteredUnreadable, 1);
        Assert.Empty(_notifier.Pushes);
    }

    private async Task StartConsumerAsync()
    {
        await _workers.StartAsync<AlertTriggeredConsumer>(Fixture.BuildServices(
            ConnectionString,
            configure: s => s.AddSingleton<IRealtimeNotifier>(_notifier),
            loggerProvider: _logs));

        await Eventually.TrueAsync(
            async () => await _broker.ConsumerCountAsync(Topology.Queue) >= 1,
            Timeout,
            "notification consumer to subscribe");
    }

    private double ConsumedCount(string outcome) =>
        _metrics.Sum(Consumed, (QueueTag, Topology.Queue), (OutcomeTag, outcome));

    private Task WaitForConsumedAsync(string outcome, int count) =>
        Eventually.TrueAsync(
            () => ConsumedCount(outcome) >= count,
            Timeout,
            $"{count} {outcome} event(s) to be counted");

    private Task PublishAsync(AlertTriggeredMessage message) =>
        _broker.PublishToQueueAsync(
            Topology.Queue,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)));

    private static string? Header(IReadOnlyBasicProperties properties, string name) =>
        properties.Headers is not null &&
        properties.Headers.TryGetValue(name, out var value) &&
        value is byte[] bytes
            ? Encoding.UTF8.GetString(bytes)
            : null;

    private static AlertTriggeredMessage NewMessage(string userId) => new()
    {
        MessageId = Guid.NewGuid(),
        AlertId = Guid.NewGuid(),
        UserId = userId,
        Symbol = "AAPL",
        Direction = AlertDirection.Above,
        TargetPrice = 100m,
        TriggerPrice = 150m,
        TriggeredAt = new DateTimeOffset(2026, 10, 12, 14, 30, 0, TimeSpan.Zero)
    };
}
