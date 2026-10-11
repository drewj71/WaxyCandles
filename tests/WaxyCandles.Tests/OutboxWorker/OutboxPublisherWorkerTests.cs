using System.Text;
using Microsoft.EntityFrameworkCore;
using WaxyCandles.Application.MarketData.Messages;
using WaxyCandles.Domain.Entities;
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
    public async Task Message_the_broker_cannot_route_is_not_marked_processed()
    {
        // With publisher confirms + mandatory, a publish that RabbitMQ can't deliver
        // fails, so the row stays pending instead of being marked processed and lost.
        await StartWorkerAsync();

        await Eventually.TrueAsync(
            async () => await _broker.MessageCountAsync(MainQueue) == 0
                && _logs.Contains("Outbox publisher started."),
            PublishTimeout,
            "worker to declare its queue");

        await _broker.DeleteTopologyAsync();
        await SeedOutboxAsync(DateTimeOffset.UtcNow, "AAPL");

        await Eventually.TrueAsync(
            () => _logs.Contains("Error while publishing outbox messages."),
            PublishTimeout,
            "publish to the deleted queue to fail");

        await using var db = NewDbContext();
        Assert.Null((await db.OutboxMessages.SingleAsync()).ProcessedAt);
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

    private async Task<List<string>> DrainPayloadsAsync()
    {
        var payloads = new List<string>();

        while (await _broker.GetAsync(MainQueue) is { } result)
        {
            payloads.Add(Encoding.UTF8.GetString(result.Body.Span));
        }

        return payloads;
    }

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
