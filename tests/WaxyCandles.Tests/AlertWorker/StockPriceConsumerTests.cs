using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using WaxyCandles.AlertWorker;
using WaxyCandles.Application.Alerts.Interfaces;
using WaxyCandles.Application.MarketData.Messages;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure.Alerts;
using WaxyCandles.Infrastructure.Persistence;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.AlertWorker;

/// <summary>
/// Runs the real StockPriceConsumer against real RabbitMQ and SQL Server, covering
/// idempotent consumption and the retry -> dead-letter path.
/// </summary>
public class StockPriceConsumerTests(IntegrationFixture fixture)
    : DatabaseTestBase(fixture)
{
    private const string MainQueue = "stock-price-updated";
    private const string RetryQueue = "stock-price-retry";
    private const string DeadLetterQueue = "stock-price-dead-letter";
    private const string RetryExchange = "stock-price-retry";
    private const string DeadLetterExchange = "stock-price-dlx";

    // The retry queue has a 5s TTL and a message gets three attempts, so the
    // full retry -> dead-letter path takes ~10s. Leave generous headroom.
    private static readonly TimeSpan MessagingTimeout = TimeSpan.FromSeconds(45);

    private readonly List<(ServiceProvider Services, StockPriceConsumer Consumer)> _consumers = [];
    private readonly CapturingLoggerProvider _logs = new();
    private readonly EvaluationProbe _probe = new();
    private IConnection _connection = default!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        _connection = await new ConnectionFactory
        {
            HostName = Fixture.RabbitMqHost,
            Port = Fixture.RabbitMqPort
        }.CreateConnectionAsync();

        await DeleteTopologyAsync();
    }

    public override async Task DisposeAsync()
    {
        await StopConsumersAsync();
        await DeleteTopologyAsync();
        await _connection.DisposeAsync();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Price_update_triggers_matching_alert_and_records_message_as_processed()
    {
        var alertId = await SeedAlertAsync("AAPL", AlertDirection.Above, 100m);
        await StartConsumerAsync();

        var message = NewMessage("AAPL", 150m);
        await PublishAsync(message);

        await WaitUntilProcessedAsync(message.MessageId);

        await using var db = NewDbContext();
        Assert.True((await db.Alerts.SingleAsync(a => a.Id == alertId)).IsTriggered);
        Assert.Equal(["AAPL"], _probe.Calls);
    }

    [Fact]
    public async Task Message_already_recorded_as_processed_is_acked_without_evaluating()
    {
        var alertId = await SeedAlertAsync("AAPL", AlertDirection.Above, 100m);
        var message = NewMessage("AAPL", 150m);

        await using (var db = NewDbContext())
        {
            db.ProcessedMessages.Add(new ProcessedMessage
            {
                Id = message.MessageId,
                ProcessedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await StartConsumerAsync();
        await PublishAsync(message);

        await WaitForSkipAsync(message.MessageId);

        await using (var db = NewDbContext())
        {
            Assert.False((await db.Alerts.SingleAsync(a => a.Id == alertId)).IsTriggered);
        }

        Assert.Empty(_probe.Calls);

        await StopConsumersAsync();
        Assert.Equal(0u, await MessageCountAsync(MainQueue));
    }

    [Fact]
    public async Task Redelivered_message_is_evaluated_only_once()
    {
        await StartConsumerAsync();

        var message = NewMessage("AAPL", 150m);

        await PublishAsync(message);
        await WaitUntilProcessedAsync(message.MessageId);

        // Simulates at-least-once redelivery of the exact same message.
        await PublishAsync(message);
        await WaitForSkipAsync(message.MessageId);

        Assert.Single(_probe.Calls);

        await using var db = NewDbContext();
        Assert.Equal(1, await db.ProcessedMessages.CountAsync(m => m.Id == message.MessageId));
    }

    [Fact]
    public async Task Transient_failure_is_retried_and_then_processed()
    {
        _probe.BeforeEvaluate = attempt => attempt == 1
            ? throw new InvalidOperationException("Simulated transient failure")
            : Task.CompletedTask;

        var alertId = await SeedAlertAsync("AAPL", AlertDirection.Above, 100m);
        await StartConsumerAsync();

        var message = NewMessage("AAPL", 150m);
        await PublishAsync(message);

        await WaitUntilProcessedAsync(message.MessageId);

        Assert.Equal(2, _probe.Calls.Count);
        Assert.True(_logs.Contains($"Message {message.MessageId} scheduled for retry 1"));

        await using var db = NewDbContext();
        Assert.True((await db.Alerts.SingleAsync(a => a.Id == alertId)).IsTriggered);
        Assert.Equal(0u, await MessageCountAsync(DeadLetterQueue));
    }

    [Fact]
    public async Task Persistent_failure_is_dead_lettered_after_three_attempts()
    {
        _probe.BeforeEvaluate = _ =>
            throw new InvalidOperationException("Simulated persistent failure");

        await StartConsumerAsync();

        var message = NewMessage("AAPL", 150m);
        var body = Serialize(message);
        await PublishAsync(body);

        await Eventually.TrueAsync(
            async () => await MessageCountAsync(DeadLetterQueue) == 1,
            MessagingTimeout,
            "message to land in the dead-letter queue");

        // Attempt 1 (retry count 0), then retries 1 and 2, then dead-letter.
        Assert.Equal(3, _probe.Calls.Count);
        Assert.True(_logs.Contains($"Message {message.MessageId} scheduled for retry 1"));
        Assert.True(_logs.Contains($"Message {message.MessageId} scheduled for retry 2"));
        Assert.True(_logs.Contains(
            $"Message {message.MessageId} has reached the maximum retry count"));

        await using (var db = NewDbContext())
        {
            Assert.False(await db.ProcessedMessages.AnyAsync(m => m.Id == message.MessageId));
        }

        await using var channel = await _connection.CreateChannelAsync();
        var deadLettered = await channel.BasicGetAsync(DeadLetterQueue, autoAck: true);

        Assert.NotNull(deadLettered);
        Assert.Equal(body, deadLettered.Body.ToArray());
        Assert.Equal(0u, await MessageCountAsync(RetryQueue));
    }

    [Fact]
    public async Task Null_payload_is_acked_and_dropped()
    {
        await StartConsumerAsync();

        await PublishAsync(Encoding.UTF8.GetBytes("null"));

        await Eventually.TrueAsync(
            () => _logs.Contains("Received invalid stock price message."),
            MessagingTimeout,
            "consumer to reject the null payload");

        await StopConsumersAsync();

        Assert.Equal(0u, await MessageCountAsync(MainQueue));
        Assert.Equal(0u, await MessageCountAsync(DeadLetterQueue));
        Assert.Empty(_probe.Calls);
    }

    [Fact(Skip = KnownIssues.ProcessedMessageCheckIsNotAtomic)]
    public async Task Concurrent_duplicate_deliveries_are_evaluated_only_once()
    {
        // Hold each evaluation open until a second one arrives (or 5s passes).
        // With a check-then-insert dedup, both deliveries pass the
        // "already processed?" check before either records the message.
        var secondArrived = new TaskCompletionSource();
        _probe.BeforeEvaluate = async attempt =>
        {
            if (attempt >= 2)
            {
                secondArrived.TrySetResult();
            }

            await Task.WhenAny(secondArrived.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        };

        // Two worker instances, as when the AlertWorker is scaled out.
        await StartConsumerAsync();
        await StartConsumerAsync();

        var message = NewMessage("AAPL", 150m);
        await PublishAsync(message);
        await PublishAsync(message);

        await WaitUntilProcessedAsync(message.MessageId);

        // Let any losing delivery go through the retry queue (5s TTL) and settle.
        await Task.Delay(TimeSpan.FromSeconds(8));

        Assert.Single(_probe.Calls);

        await using var db = NewDbContext();
        Assert.Equal(1, await db.ProcessedMessages.CountAsync(m => m.Id == message.MessageId));
        Assert.Equal(0u, await MessageCountAsync(DeadLetterQueue));
    }

    [Fact(Skip = KnownIssues.MalformedPayloadIsNeverAcked)]
    public async Task Malformed_json_payload_is_not_left_unacknowledged()
    {
        await StartConsumerAsync();

        await PublishAsync(Encoding.UTF8.GetBytes("{ this is not json"));

        // A valid message published afterwards proves the consumer is still running.
        var valid = NewMessage("AAPL", 150m);
        await PublishAsync(valid);
        await WaitUntilProcessedAsync(valid.MessageId);

        // Closing the channel requeues anything still unacked.
        await StopConsumersAsync();

        Assert.Equal(0u, await MessageCountAsync(MainQueue));
    }

    private async Task StartConsumerAsync()
    {
        var services = Fixture.BuildServices(
            ConnectionString,
            configure: s => s.AddScoped<IAlertEvaluationService>(sp =>
                new ProbingAlertEvaluationService(
                    new AlertEvaluationService(sp.GetRequiredService<AppDbContext>()),
                    _probe)),
            loggerProvider: _logs);

        var consumer = ActivatorUtilities.CreateInstance<StockPriceConsumer>(services);
        _consumers.Add((services, consumer));

        await consumer.StartAsync(CancellationToken.None);

        var expectedConsumers = _consumers.Count;

        await Eventually.TrueAsync(
            async () => await ConsumerCountAsync(MainQueue) >= expectedConsumers,
            MessagingTimeout,
            $"{expectedConsumers} consumer(s) to subscribe to {MainQueue}");
    }

    private async Task StopConsumersAsync()
    {
        foreach (var (services, consumer) in _consumers)
        {
            await consumer.StopAsync(CancellationToken.None);
            consumer.Dispose();
            await services.DisposeAsync();
        }

        _consumers.Clear();
    }

    private Task WaitUntilProcessedAsync(Guid messageId) =>
        Eventually.TrueAsync(
            async () =>
            {
                await using var db = NewDbContext();
                return await db.ProcessedMessages.AnyAsync(m => m.Id == messageId);
            },
            MessagingTimeout,
            $"message {messageId} to be recorded as processed");

    private Task WaitForSkipAsync(Guid messageId) =>
        Eventually.TrueAsync(
            () => _logs.Contains($"Message {messageId} has already been processed"),
            MessagingTimeout,
            $"consumer to skip already-processed message {messageId}");

    private async Task<Guid> SeedAlertAsync(
        string symbol,
        AlertDirection direction,
        decimal targetPrice)
    {
        var alert = new Alert
        {
            Id = Guid.NewGuid(),
            UserId = "user-1",
            Symbol = symbol,
            Direction = direction,
            TargetPrice = targetPrice
        };

        await using var db = NewDbContext();
        db.Alerts.Add(alert);
        await db.SaveChangesAsync();

        return alert.Id;
    }

    private static StockPriceUpdatedMessage NewMessage(string symbol, decimal price) => new()
    {
        MessageId = Guid.NewGuid(),
        Symbol = symbol,
        Price = price,
        Timestamp = DateTimeOffset.UtcNow
    };

    private static byte[] Serialize(StockPriceUpdatedMessage message) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));

    private Task PublishAsync(StockPriceUpdatedMessage message) =>
        PublishAsync(Serialize(message));

    private async Task PublishAsync(byte[] body)
    {
        await using var channel = await _connection.CreateChannelAsync();

        await channel.BasicPublishAsync(
            exchange: string.Empty,
            routingKey: MainQueue,
            mandatory: false,
            basicProperties: new BasicProperties { Persistent = true },
            body: body);
    }

    private async Task<uint> MessageCountAsync(string queue) =>
        (await DeclarePassiveAsync(queue))?.MessageCount ?? 0;

    private async Task<uint> ConsumerCountAsync(string queue) =>
        (await DeclarePassiveAsync(queue))?.ConsumerCount ?? 0;

    private async Task<QueueDeclareOk?> DeclarePassiveAsync(string queue)
    {
        // A passive declare of a missing queue closes the channel, so use a throwaway one.
        await using var channel = await _connection.CreateChannelAsync();

        try
        {
            return await channel.QueueDeclarePassiveAsync(queue);
        }
        catch (OperationInterruptedException)
        {
            return null;
        }
    }

    private async Task DeleteTopologyAsync()
    {
        // The consumer hardcodes these names, so each test starts from a clean broker.
        foreach (var queue in new[] { MainQueue, RetryQueue, DeadLetterQueue })
        {
            await using var channel = await _connection.CreateChannelAsync();
            await channel.QueueDeleteAsync(queue);
        }

        foreach (var exchange in new[] { RetryExchange, DeadLetterExchange })
        {
            await using var channel = await _connection.CreateChannelAsync();
            await channel.ExchangeDeleteAsync(exchange);
        }
    }
}
