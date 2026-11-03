using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using WaxyCandles.AlertWorker;
using WaxyCandles.Application.MarketData.Messages;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure;
using WaxyCandles.Infrastructure.Messaging;
using WaxyCandles.OutboxWorker;
using WaxyCandles.Tests.Infrastructure;
using static WaxyCandles.Tests.Infrastructure.RabbitMqTestBroker;
using static WaxyCandles.Tests.Resilience.FaultInjection;

namespace WaxyCandles.Tests.Resilience;

/// <summary>
/// REL-2 through RabbitMQ: SQL retries happen inside one delivery, and only
/// when they're used up does the queue's own retry and dead-letter path run.
/// </summary>
public class SqlRetryMessagingTests(IntegrationFixture fixture) : DatabaseTestBase(fixture)
{
    private static readonly TimeSpan MessagingTimeout = TimeSpan.FromSeconds(60);

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
    public async Task Persistent_sql_fault_uses_up_sql_retries_per_delivery_then_dead_letters()
    {
        await SeedAlertAsync();

        // Every read of Alerts fails: 4 attempts (1 + 3 retries) per delivery,
        // and 3 deliveries (1 + the queue's 2 retries).
        var fault = new CommandFault(Sql("SELECT", "[Alerts]"), times: int.MaxValue);

        await _workers.StartAsync<StockPriceConsumer>(Services([fault]));
        await Eventually.TrueAsync(
            async () => await _broker.ConsumerCountAsync(MainQueue) >= 1,
            MessagingTimeout,
            "consumer to subscribe");

        var message = new StockPriceUpdatedMessage
        {
            MessageId = Guid.NewGuid(),
            Symbol = "AAPL",
            Price = 150m,
            Timestamp = DateTimeOffset.UtcNow
        };

        await _broker.PublishAsync(message);

        await Eventually.TrueAsync(
            async () => await _broker.MessageCountAsync(DeadLetterQueue) == 1,
            MessagingTimeout,
            "message to be dead-lettered");

        Assert.Equal(12, fault.Thrown);

        var deadLettered = await _broker.GetAsync(DeadLetterQueue);
        Assert.NotNull(deadLettered);

        // The root cause, not EF's RetryLimitExceededException wrapper.
        Assert.Equal(
            "Microsoft.Data.SqlClient.SqlException",
            Header(deadLettered.BasicProperties, DeadLetterHeaders.ExceptionType));

        await using var db = NewDbContext();
        Assert.False(await db.ProcessedMessages.AnyAsync());
    }

    [Fact]
    public async Task Outbox_mark_after_an_ambiguous_commit_does_not_republish()
    {
        var first = await SeedOutboxAsync("AAPL");
        var second = await SeedOutboxAsync("MSFT");

        // The publisher's save of ProcessedAt commits, then reports a
        // transient error; the retry re-applies the same update.
        var fault = new CommitFault(afterCommit: true);

        await _workers.StartAsync<OutboxPublisherWorker>(Services([fault]));

        await Eventually.TrueAsync(
            async () =>
            {
                await using var db = NewDbContext();
                return !await db.OutboxMessages.AnyAsync(m => m.ProcessedAt == null);
            },
            MessagingTimeout,
            "outbox rows to be marked processed");

        // At least one more poll (every 5s), which would republish anything
        // left unmarked.
        await Task.Delay(TimeSpan.FromSeconds(7));

        Assert.Equal(1, fault.Thrown);

        var payloads = new List<string>();
        while (await _broker.GetAsync(MainQueue) is { } result)
        {
            payloads.Add(Encoding.UTF8.GetString(result.Body.Span));
        }

        Assert.Equal([first.Payload, second.Payload], payloads);
    }

    private ServiceProvider Services(IInterceptor[] interceptors)
    {
        var settings = Fixture.TestSettings(ConnectionString);

        // Short SQL retry waits, so the test doesn't wait out the real backoff.
        settings["Database:MaxRetryDelay"] = "00:00:00.050";

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(logging => logging.AddProvider(_logs));
        services.AddInfrastructure(configuration);
        services.AddDbInterceptors(interceptors);

        return services.BuildServiceProvider(validateScopes: true);
    }

    private static string? Header(IReadOnlyBasicProperties properties, string key) =>
        properties.Headers is { } headers &&
        headers.TryGetValue(key, out var value) &&
        value is byte[] bytes
            ? Encoding.UTF8.GetString(bytes)
            : null;

    private async Task SeedAlertAsync()
    {
        await using var db = NewDbContext();
        db.Alerts.Add(new Alert
        {
            Id = Guid.NewGuid(),
            UserId = "user-1",
            Symbol = "AAPL",
            Direction = AlertDirection.Above,
            TargetPrice = 100m
        });
        await db.SaveChangesAsync();
    }

    private async Task<OutboxMessage> SeedOutboxAsync(string symbol)
    {
        var message = new StockPriceUpdatedMessage
        {
            MessageId = Guid.NewGuid(),
            Symbol = symbol,
            Price = 123.45m,
            Timestamp = DateTimeOffset.UtcNow
        };

        var entry = new OutboxMessage
        {
            Id = message.MessageId,
            Type = StockPriceUpdatedMessage.MessageType,
            Payload = Encoding.UTF8.GetString(Serialize(message)),
            // Distinct, so the publish order is known.
            CreatedAt = DateTimeOffset.UtcNow.AddMilliseconds(symbol == "AAPL" ? -10 : 0)
        };

        await using var db = NewDbContext();
        db.OutboxMessages.Add(entry);
        await db.SaveChangesAsync();

        return entry;
    }
}
