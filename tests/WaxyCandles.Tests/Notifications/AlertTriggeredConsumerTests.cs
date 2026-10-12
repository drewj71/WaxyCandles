using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WaxyCandles.Application.Alerts.Messages;
using WaxyCandles.Application.Notifications.Interfaces;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure.Messaging;
using WaxyCandles.Infrastructure.Notifications;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Notifications;

/// <summary>
/// The real AlertTriggeredConsumer against RabbitMQ and SQL Server. Transport
/// behaviour (retry, dead-letter, recovery) is shared with StockPriceConsumer and
/// covered there; these cover what's specific to notifications.
/// </summary>
public class AlertTriggeredConsumerTests(IntegrationFixture fixture)
    : DatabaseTestBase(fixture)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly QueueTopology Topology = QueueTopology.AlertTriggered;

    private readonly RunningWorkers _workers = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly RecordingNotifier _notifier = new();
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
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Triggered_alert_is_stored_as_a_notification_and_pushed_to_its_owner()
    {
        await StartConsumerAsync();

        var message = NewMessage(_userId, AlertDirection.Below, target: 0.0525m, trigger: 0.05m);
        await PublishAsync(message);

        await WaitForLogAsync($"Successfully processed message {message.MessageId}");

        await using var db = NewDbContext();
        var stored = await db.Notifications.SingleAsync();

        Assert.Equal(_userId, stored.UserId);
        Assert.Equal(NotificationType.AlertTriggered, stored.Type);
        Assert.Equal(message.AlertId, stored.AlertId);
        Assert.Equal(message.MessageId, stored.SourceMessageId);
        Assert.Equal("SIRI alert triggered", stored.Title);
        Assert.Equal("SIRI fell below $0.0525 (now $0.05).", stored.Body);
        Assert.Equal(message.TriggeredAt, stored.CreatedAt);
        Assert.Null(stored.ReadAt);

        var (pushedTo, pushed) = Assert.Single(_notifier.Pushes);
        Assert.Equal(_userId, pushedTo);
        Assert.Equal(stored.Id, pushed.Id);
    }

    [Fact]
    public async Task Redelivered_event_creates_one_notification_and_pushes_once()
    {
        await StartConsumerAsync();

        var message = NewMessage(_userId);
        await PublishAsync(message);
        await WaitForLogAsync($"Successfully processed message {message.MessageId}");

        await PublishAsync(message);
        await WaitForLogAsync($"Message {message.MessageId} has already been processed");

        await using var db = NewDbContext();
        Assert.Equal(1, await db.Notifications.CountAsync());
        Assert.Single(_notifier.Pushes);
    }

    [Fact]
    public async Task Failed_push_does_not_fail_the_message()
    {
        // The notification is already stored; retrying would only find it as a
        // duplicate, so a push failure is logged and the message acked.
        _notifier.Fail = true;
        await StartConsumerAsync();

        var message = NewMessage(_userId);
        await PublishAsync(message);

        await WaitForLogAsync($"Successfully processed message {message.MessageId}");

        Assert.True(_logs.Contains("Could not push notification"));
        Assert.False(_logs.Contains($"Message {message.MessageId} scheduled for retry"));

        await using var db = NewDbContext();
        Assert.Equal(1, await db.Notifications.CountAsync());
    }

    [Fact]
    public async Task Stalled_push_times_out_without_holding_up_the_queue()
    {
        // Deliveries are handled one at a time and the push happens before the
        // ack, so an unbounded push would block every notification behind it.
        _notifier.Hang = true;
        await StartConsumerAsync();

        var first = NewMessage(_userId);
        var second = NewMessage(_userId);
        await PublishAsync(first);
        await PublishAsync(second);

        var started = DateTime.UtcNow;
        await WaitForLogAsync($"Successfully processed message {second.MessageId}");

        // Two ~5s push timeouts, well short of a client-side timeout (~30s).
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.Zero, TimeSpan.FromSeconds(20));
        Assert.False(_logs.Contains("scheduled for retry"));

        await using var db = NewDbContext();
        Assert.Equal(2, await db.Notifications.CountAsync());
    }

    [Fact]
    public async Task Event_for_a_user_who_no_longer_exists_is_skipped_not_retried()
    {
        await StartConsumerAsync();

        var message = NewMessage(userId: "deleted-user");
        await PublishAsync(message);

        await WaitForLogAsync($"Message {message.MessageId} skipped");

        Assert.True(_logs.Contains("who no longer exists"));
        Assert.False(_logs.Contains($"Message {message.MessageId} scheduled for retry"));
        Assert.Empty(_notifier.Pushes);

        await using var db = NewDbContext();
        Assert.Equal(0, await db.Notifications.CountAsync());
        Assert.Equal(0u, await _broker.MessageCountAsync(Topology.DeadLetterQueue));
    }

    public static TheoryData<string> InvalidEvents()
    {
        var withoutUser = NewMessage(userId: "");

        // 0 is not a defined AlertDirection (Above = 1, Below = 2).
        var undefinedDirection = NewMessage(userId: "user");
        undefinedDirection.Direction = 0;

        return new()
        {
            JsonSerializer.Serialize(withoutUser),
            JsonSerializer.Serialize(undefinedDirection)
        };
    }

    [Theory]
    [MemberData(nameof(InvalidEvents))]
    public async Task Invalid_event_is_dead_lettered(string payload)
    {
        await StartConsumerAsync();

        var body = Encoding.UTF8.GetBytes(payload);
        await _broker.PublishToQueueAsync(Topology.Queue, body);

        await Eventually.TrueAsync(
            async () => await _broker.MessageCountAsync(Topology.DeadLetterQueue) == 1,
            Timeout,
            "invalid event to be dead-lettered");

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

    private Task PublishAsync(AlertTriggeredMessage message) =>
        _broker.PublishToQueueAsync(
            Topology.Queue,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)));

    private Task WaitForLogAsync(string fragment) =>
        Eventually.TrueAsync(() => _logs.Contains(fragment), Timeout, fragment);

    private static AlertTriggeredMessage NewMessage(
        string userId,
        AlertDirection direction = AlertDirection.Above,
        decimal target = 100m,
        decimal trigger = 150m) => new()
    {
        MessageId = Guid.NewGuid(),
        AlertId = Guid.NewGuid(),
        UserId = userId,
        Symbol = direction == AlertDirection.Below ? "SIRI" : "AAPL",
        Direction = direction,
        TargetPrice = target,
        TriggerPrice = trigger,
        TriggeredAt = new DateTimeOffset(2026, 10, 12, 14, 30, 0, TimeSpan.Zero)
    };
}
