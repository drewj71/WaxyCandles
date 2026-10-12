using System.Text;
using Microsoft.EntityFrameworkCore;
using WaxyCandles.Application.Alerts.Messages;
using WaxyCandles.Application.MarketData.Messages;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Infrastructure.Messaging;
using WaxyCandles.OutboxWorker;
using WaxyCandles.Tests.Infrastructure;
using static WaxyCandles.Tests.Infrastructure.RabbitMqTestBroker;

namespace WaxyCandles.Tests.OutboxWorker;

/// <summary>
/// The publish side of the outbox pattern, run against real SQL Server and RabbitMQ.
/// </summary>
public class OutboxPublisherWorkerTests(IntegrationFixture fixture)
    : DatabaseTestBase(fixture)
{
    // The worker polls every 5s and publishes at most 20 messages per poll.
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(30);

    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 14, 30, 0, TimeSpan.Zero);

    private readonly RunningWorkers _workers = new();
    private readonly CapturingLoggerProvider _logs = new();
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
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Unprocessed_messages_are_published_verbatim_and_marked_processed()
    {
        var first = await SeedOutboxAsync(T0, "AAPL");
        var second = await SeedOutboxAsync(T0.AddSeconds(1), "MSFT");

        var before = DateTimeOffset.UtcNow;
        await StartWorkerAsync();
        await WaitUntilAllProcessedAsync();

        Assert.Equal(
            [first.Payload, second.Payload],
            await DrainPayloadsAsync());

        await using var db = NewDbContext();
        Assert.All(
            await db.OutboxMessages.ToListAsync(),
            m => Assert.InRange(m.ProcessedAt!.Value, before, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Already_processed_messages_are_not_republished()
    {
        await SeedOutboxAsync(T0, "AAPL", processedAt: T0.AddSeconds(1));
        var pending = await SeedOutboxAsync(T0.AddSeconds(2), "MSFT");

        await StartWorkerAsync();
        await WaitUntilAllProcessedAsync();

        Assert.Equal([pending.Payload], await DrainPayloadsAsync());
    }

    [Fact]
    public async Task Backlog_larger_than_one_batch_is_published_oldest_first()
    {
        // Inserted out of order so ordering has to come from CreatedAt, not insertion.
        var offsets = Enumerable.Range(0, 25).Reverse().ToList();
        var seeded = new List<OutboxMessage>();

        foreach (var offset in offsets)
        {
            seeded.Add(await SeedOutboxAsync(T0.AddSeconds(offset), $"SYM{offset:00}"));
        }

        await StartWorkerAsync();
        await WaitUntilAllProcessedAsync();

        Assert.Equal(
            seeded.OrderBy(m => m.CreatedAt).Select(m => m.Payload).ToList(),
            await DrainPayloadsAsync());
    }

    [Fact]
    public async Task Messages_published_later_are_picked_up_by_the_next_poll()
    {
        await StartWorkerAsync();

        // Let the first (empty) poll happen, then add a message.
        await Task.Delay(TimeSpan.FromSeconds(1));
        var late = await SeedOutboxAsync(DateTimeOffset.UtcNow, "AAPL");

        await WaitUntilAllProcessedAsync();

        Assert.Equal([late.Payload], await DrainPayloadsAsync());
    }

    [Fact]
    public async Task Published_messages_are_persistent_and_carry_the_outbox_id()
    {
        var entry = await SeedOutboxAsync(T0, "AAPL");

        await StartWorkerAsync();
        await WaitUntilAllProcessedAsync();

        var delivered = await _broker.GetAsync(MainQueue);

        Assert.NotNull(delivered);
        Assert.True(
            delivered.BasicProperties.Persistent,
            "Outbox messages are marked processed once published, so they must be " +
            "persistent or a RabbitMQ restart loses them.");
        Assert.Equal(entry.Id.ToString(), delivered.BasicProperties.MessageId);
        Assert.Equal(nameof(StockPriceUpdatedMessage), delivered.BasicProperties.Type);
        Assert.Equal("application/json", delivered.BasicProperties.ContentType);
    }

    [Fact]
    public async Task Unroutable_message_stays_pending_until_the_queue_is_redeclared()
    {
        // With publisher confirms + mandatory, a publish that RabbitMQ can't route
        // fails, so the row stays pending instead of being marked processed and
        // lost. The worker then redeclares the queue and sends it on a later poll.
        await StartWorkerAsync();

        await Eventually.TrueAsync(
            () => _broker.QueueExistsAsync(MainQueue),
            PublishTimeout,
            "worker to declare its queue");

        await _broker.DeleteQueueAsync(MainQueue);
        var entry = await SeedOutboxAsync(DateTimeOffset.UtcNow, "AAPL");

        await Eventually.TrueAsync(
            () => _logs.Contains("Error while publishing outbox messages."),
            PublishTimeout,
            "publish to the deleted queue to fail");

        // The next poll is 5s away, so the failed attempt is observable here.
        await using (var db = NewDbContext())
        {
            Assert.Null((await db.OutboxMessages.SingleAsync()).ProcessedAt);
        }

        await WaitUntilAllProcessedAsync();
        Assert.Equal([entry.Payload], await DrainPayloadsAsync());
    }

    [Fact]
    public async Task Backlog_drains_without_waiting_between_full_batches()
    {
        // 45 messages = two full batches and a partial one. Without draining,
        // the worker would wait the 5s poll interval after each batch.
        for (var i = 0; i < 45; i++)
        {
            await SeedOutboxAsync(T0.AddSeconds(i), $"SYM{i:00}");
        }

        var started = DateTime.UtcNow;
        await StartWorkerAsync();
        await WaitUntilAllProcessedAsync();

        Assert.True(
            DateTime.UtcNow - started < TimeSpan.FromSeconds(4.5),
            "A backlog of full batches should not wait a poll interval between them.");
        Assert.Equal(45, (await DrainPayloadsAsync()).Count);
    }
    [Fact]
    public async Task Alert_triggered_events_are_routed_to_their_own_queue()
    {
        var price = await SeedOutboxAsync(T0, "AAPL");
        var alert = await SeedRawOutboxAsync(
            nameof(AlertTriggeredMessage),
            """{ "MessageId": "6a0c1d0e-0000-4000-8000-000000000001", "Symbol": "AAPL" }""",
            T0.AddSeconds(1));

        await StartWorkerAsync();
        await WaitUntilAllProcessedAsync();

        Assert.Equal([price.Payload], await DrainPayloadsAsync(MainQueue));
        Assert.Equal([alert.Payload], await DrainPayloadsAsync(QueueTopology.AlertTriggered.Queue));
    }

    [Fact]
    public async Task Type_is_matched_like_the_database_does_ignoring_case_and_trailing_spaces()
    {
        // SQL Server's default collation selects these as routed; the lookup must
        // agree, or the row would throw and block every batch it heads.
        var oddlyCased = await SeedRawOutboxAsync(
            "alerttriggeredmessage ",
            "{}",
            T0);
        var price = await SeedOutboxAsync(T0.AddSeconds(1), "AAPL");

        await StartWorkerAsync();
        await WaitUntilAllProcessedAsync();

        Assert.Equal([oddlyCased.Payload], await DrainPayloadsAsync(QueueTopology.AlertTriggered.Queue));
        Assert.Equal([price.Payload], await DrainPayloadsAsync());
    }

    [Fact]
    public async Task Row_with_an_unrouted_type_stays_pending_and_does_not_block_others()
    {
        // Older than every routable row, so it would be first in line.
        var unknown = await SeedRawOutboxAsync("SomethingNobodyRoutes", "{}", T0);
        var price = await SeedOutboxAsync(T0.AddSeconds(1), "AAPL");

        await StartWorkerAsync();
        await WaitUntilProcessedAsync(price.Id);

        Assert.Equal([price.Payload], await DrainPayloadsAsync());
        Assert.True(_logs.Contains("have a type with no route"));

        await using var db = NewDbContext();
        Assert.Null((await db.OutboxMessages.SingleAsync(m => m.Id == unknown.Id)).ProcessedAt);
    }

    private async Task StartWorkerAsync() =>
        await _workers.StartAsync<OutboxPublisherWorker>(
            Fixture.BuildServices(ConnectionString, loggerProvider: _logs));

    private Task WaitUntilAllProcessedAsync() =>
        Eventually.TrueAsync(
            async () =>
            {
                await using var db = NewDbContext();
                return await db.OutboxMessages.AnyAsync()
                    && !await db.OutboxMessages.AnyAsync(m => m.ProcessedAt == null);
            },
            PublishTimeout,
            "all outbox messages to be marked processed");

    private Task<List<string>> DrainPayloadsAsync() => DrainPayloadsAsync(MainQueue);

    private async Task<List<string>> DrainPayloadsAsync(string queue)
    {
        var payloads = new List<string>();

        while (await _broker.GetAsync(queue) is { } result)
        {
            payloads.Add(Encoding.UTF8.GetString(result.Body.Span));
        }

        return payloads;
    }

    private async Task<OutboxMessage> SeedRawOutboxAsync(
        string type,
        string payload,
        DateTimeOffset createdAt)
    {
        var entry = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = type,
            Payload = payload,
            CreatedAt = createdAt
        };

        await using var db = NewDbContext();
        db.OutboxMessages.Add(entry);
        await db.SaveChangesAsync();

        return entry;
    }

    private Task WaitUntilProcessedAsync(Guid outboxId) =>
        Eventually.TrueAsync(
            async () =>
            {
                await using var db = NewDbContext();
                return await db.OutboxMessages.AnyAsync(
                    m => m.Id == outboxId && m.ProcessedAt != null);
            },
            PublishTimeout,
            $"outbox message {outboxId} to be marked processed");

    private async Task<OutboxMessage> SeedOutboxAsync(
        DateTimeOffset createdAt,
        string symbol,
        DateTimeOffset? processedAt = null)
    {
        var message = new StockPriceUpdatedMessage
        {
            MessageId = Guid.NewGuid(),
            Symbol = symbol,
            Price = 123.45m,
            Timestamp = createdAt
        };

        var entry = new OutboxMessage
        {
            Id = message.MessageId,
            Type = nameof(StockPriceUpdatedMessage),
            Payload = Encoding.UTF8.GetString(Serialize(message)),
            CreatedAt = createdAt,
            ProcessedAt = processedAt
        };

        await using var db = NewDbContext();
        db.OutboxMessages.Add(entry);
        await db.SaveChangesAsync();

        return entry;
    }
}
