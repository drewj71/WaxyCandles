using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WaxyCandles.AlertWorker;
using WaxyCandles.Application.Alerts.Interfaces;
using WaxyCandles.Application.MarketData.Messages;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure.Alerts;
using WaxyCandles.Infrastructure.Persistence;
using WaxyCandles.Tests.Infrastructure;
using static WaxyCandles.Tests.Infrastructure.RabbitMqTestBroker;

namespace WaxyCandles.Tests.AlertWorker;

/// <summary>
/// Runs the real StockPriceConsumer against real RabbitMQ and SQL Server, covering
/// idempotent consumption and the retry -> dead-letter path.
/// </summary>
public class StockPriceConsumerTests(IntegrationFixture fixture)
    : DatabaseTestBase(fixture)
{
    // The retry queue has a 5s TTL and a message gets three attempts, so the
    // full retry -> dead-letter path takes ~10s. Leave generous headroom.
    private static readonly TimeSpan MessagingTimeout = TimeSpan.FromSeconds(45);

    private readonly RunningWorkers _workers = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly EvaluationProbe _probe = new();
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
    public async Task Price_update_triggers_matching_alert_and_records_message_as_processed()
    {
        var alertId = await SeedAlertAsync("AAPL", AlertDirection.Above, 100m);
        await StartConsumerAsync();

        var message = NewMessage("AAPL", 150m);
        await _broker.PublishAsync(message);

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
        await _broker.PublishAsync(message);

        await WaitForSkipAsync(message.MessageId);

        await using (var db = NewDbContext())
        {
            Assert.False((await db.Alerts.SingleAsync(a => a.Id == alertId)).IsTriggered);
        }

        Assert.Empty(_probe.Calls);

        await _workers.StopAllAsync();
        Assert.Equal(0u, await _broker.MessageCountAsync(MainQueue));
    }

    [Fact]
    public async Task Redelivered_message_is_evaluated_only_once()
    {
        await StartConsumerAsync();

        var message = NewMessage("AAPL", 150m);

        await _broker.PublishAsync(message);
        await WaitUntilProcessedAsync(message.MessageId);

        // Simulates at-least-once redelivery of the exact same message.
        await _broker.PublishAsync(message);
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
        await _broker.PublishAsync(message);

        await WaitUntilProcessedAsync(message.MessageId);

        // The failure happens after the ProcessedMessage claim is inserted, so this
        // also proves the rollback releases the claim: otherwise the retry would be
        // skipped as a duplicate and the alert never evaluated.
        Assert.Equal(2, _probe.Calls.Count);
        Assert.True(_logs.Contains($"Message {message.MessageId} scheduled for retry 1"));
        Assert.False(_logs.Contains($"Message {message.MessageId} has already been processed"));

        await using var db = NewDbContext();
        Assert.True((await db.Alerts.SingleAsync(a => a.Id == alertId)).IsTriggered);
        Assert.Equal(0u, await _broker.MessageCountAsync(DeadLetterQueue));
    }

    [Fact]
    public async Task Persistent_failure_is_dead_lettered_after_three_attempts()
    {
        _probe.BeforeEvaluate = _ =>
            throw new InvalidOperationException("Simulated persistent failure");

        await StartConsumerAsync();

        var message = NewMessage("AAPL", 150m);
        var body = Serialize(message);
        await _broker.PublishAsync(body);

        await Eventually.TrueAsync(
            async () => await _broker.MessageCountAsync(DeadLetterQueue) == 1,
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

        var deadLettered = await _broker.GetAsync(DeadLetterQueue);

        Assert.NotNull(deadLettered);
        Assert.Equal(body, deadLettered.Body.ToArray());
        Assert.True(deadLettered.BasicProperties.Persistent);
        Assert.Equal(0u, await _broker.MessageCountAsync(RetryQueue));
    }

    public static TheoryData<string> UnreadablePayloads =>
    [
        "null",
        "{ this is not json",
        "{}",
        """{ "MessageId": "00000000-0000-0000-0000-000000000000", "Symbol": "AAPL", "Price": 1 }""",
        """{ "MessageId": "8d3c6a52-6f4e-4a8b-9a3e-2f1c5b7d9e01", "Symbol": " ", "Price": 1 }"""
    ];

    [Theory]
    [MemberData(nameof(UnreadablePayloads))]
    public async Task Unreadable_payload_is_dead_lettered_without_retrying(string payload)
    {
        await StartConsumerAsync();

        var body = Encoding.UTF8.GetBytes(payload);
        await _broker.PublishAsync(body);

        // A valid message published afterwards proves the consumer is still running.
        var valid = NewMessage("AAPL", 150m);
        await _broker.PublishAsync(valid);
        await WaitUntilProcessedAsync(valid.MessageId);

        // Closing the channel requeues anything still unacked.
        await _workers.StopAllAsync();

        Assert.Equal(0u, await _broker.MessageCountAsync(MainQueue));
        Assert.Equal(0u, await _broker.MessageCountAsync(RetryQueue));

        var deadLettered = await _broker.GetAsync(DeadLetterQueue);

        Assert.NotNull(deadLettered);
        Assert.Equal(body, deadLettered.Body.ToArray());
        Assert.True(deadLettered.BasicProperties.Persistent);
        Assert.Equal(["AAPL"], _probe.Calls);
    }

    [Fact]
    public async Task Concurrent_duplicate_deliveries_are_evaluated_only_once()
    {
        // Hold each evaluation open until a second one arrives (or 5s passes).
        // With a check-then-insert dedup, both deliveries would pass the
        // "already processed?" check before either records the message. With the
        // claim, the second delivery blocks on the insert and never evaluates.
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
        await _broker.PublishAsync(message);
        await _broker.PublishAsync(message);

        await WaitUntilProcessedAsync(message.MessageId);
        await WaitForSkipAsync(message.MessageId);

        Assert.Single(_probe.Calls);
        Assert.False(_logs.Contains($"Message {message.MessageId} scheduled for retry"));

        await using var db = NewDbContext();
        Assert.Equal(1, await db.ProcessedMessages.CountAsync(m => m.Id == message.MessageId));
        Assert.Equal(0u, await _broker.MessageCountAsync(DeadLetterQueue));
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

        await _workers.StartAsync<StockPriceConsumer>(services);

        var expectedConsumers = _workers.Count;

        await Eventually.TrueAsync(
            async () => await _broker.ConsumerCountAsync(MainQueue) >= expectedConsumers,
            MessagingTimeout,
            $"{expectedConsumers} consumer(s) to subscribe to {MainQueue}");
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
}
