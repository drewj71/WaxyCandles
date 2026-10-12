using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WaxyCandles.AlertWorker;
using WaxyCandles.Application.Alerts.Interfaces;
using WaxyCandles.Application.Alerts.Messages;
using WaxyCandles.Application.MarketData.Messages;
using WaxyCandles.Application.Notifications.Interfaces;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure.Alerts;
using WaxyCandles.Infrastructure.MarketData;
using WaxyCandles.Infrastructure.Messaging;
using WaxyCandles.Infrastructure.Notifications;
using WaxyCandles.Infrastructure.Persistence;
using WaxyCandles.OutboxWorker;
using WaxyCandles.Tests.AlertWorker;
using WaxyCandles.Tests.Infrastructure;
using WaxyCandles.Tests.MarketData;
using WaxyCandles.Tests.Notifications;
using static WaxyCandles.Tests.Infrastructure.RabbitMqTestBroker;

namespace WaxyCandles.Tests.Pipeline;

/// <summary>
/// End to end: price poll -> StockPrice + OutboxMessage -> OutboxWorker -> RabbitMQ
/// -> StockPriceConsumer -> alert triggered + AlertTriggered outbox event
/// -> OutboxWorker -> RabbitMQ -> AlertTriggeredConsumer -> notification stored and
/// pushed. Only the market data provider and the realtime push are faked.
/// </summary>
public class PricePipelineTests(IntegrationFixture fixture)
    : DatabaseTestBase(fixture)
{
    private static readonly TimeSpan PipelineTimeout = TimeSpan.FromSeconds(30);

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
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Polled_price_triggers_the_alert_and_notifies_its_owner()
    {
        var hit = await SeedAlertAsync("AAPL", AlertDirection.Above, 100m);
        var miss = await SeedAlertAsync("MSFT", AlertDirection.Below, 200m);
        _provider.SetQuote("AAPL", 150m);
        _provider.SetQuote("MSFT", 300m);

        await StartPipelineAsync();
        await PollPricesAsync();

        var messageIds = await PriceMessageIdsAsync();
        Assert.Equal(2, messageIds.Count);

        await WaitUntilProcessedAsync(messageIds);

        await using (var db = NewDbContext())
        {
            Assert.True((await db.Alerts.SingleAsync(a => a.Id == hit)).IsTriggered);
            Assert.False((await db.Alerts.SingleAsync(a => a.Id == miss)).IsTriggered);
        }

        // The triggered alert's event goes back out through the outbox (next poll,
        // up to 5s) to the notification consumer.
        await Eventually.TrueAsync(
            () => _notifier.Pushes.Count == 1,
            PipelineTimeout,
            "the alert's owner to be notified");

        var (pushedTo, pushed) = Assert.Single(_notifier.Pushes);
        Assert.Equal(_userId, pushedTo);
        Assert.Equal(hit, pushed.AlertId);
        Assert.Equal("AAPL alert triggered", pushed.Title);
        Assert.Equal("AAPL rose above $100.00 (now $150.00).", pushed.Body);

        await using var verifyDb = NewDbContext();
        var stored = await verifyDb.Notifications.SingleAsync();
        Assert.Equal(pushed.Id, stored.Id);
        Assert.Equal(_userId, stored.UserId);
        Assert.Null(stored.ReadAt);

        Assert.False(await verifyDb.OutboxMessages.AnyAsync(m => m.ProcessedAt == null));
        Assert.Equal(0u, await _broker.MessageCountAsync(DeadLetterQueue));
        Assert.Equal(0u, await _broker.MessageCountAsync(QueueTopology.AlertTriggered.DeadLetterQueue));
    }

    [Fact]
    public async Task Outbox_republish_after_a_crash_is_absorbed_by_the_idempotent_consumer()
    {
        await SeedAlertAsync("AAPL", AlertDirection.Above, 100m);
        _provider.SetQuote("AAPL", 150m);

        await StartPipelineAsync();
        await PollPricesAsync();

        var messageId = Assert.Single(await PriceMessageIdsAsync());
        await WaitUntilProcessedAsync([messageId]);

        // Simulate the outbox worker crashing after publishing but before saving
        // ProcessedAt: the row looks unpublished and gets sent again.
        await using (var db = NewDbContext())
        {
            var entry = await db.OutboxMessages.SingleAsync(m => m.Id == messageId);
            entry.ProcessedAt = null;
            await db.SaveChangesAsync();
        }

        await Eventually.TrueAsync(
            () => _logs.Contains($"Message {messageId} has already been processed"),
            PipelineTimeout,
            "consumer to skip the republished message");

        Assert.Single(_probe.Calls);

        await using var verifyDb = NewDbContext();
        Assert.Equal(1, await verifyDb.ProcessedMessages.CountAsync());

        // Evaluated once, so exactly one AlertTriggered event and one notification.
        Assert.Equal(1, await verifyDb.OutboxMessages.CountAsync(
            m => m.Type == nameof(AlertTriggeredMessage)));

        await Eventually.TrueAsync(
            async () =>
            {
                await using var db = NewDbContext();
                return await db.Notifications.CountAsync() == 1;
            },
            PipelineTimeout,
            "the single notification to be created");
    }

    private async Task StartPipelineAsync()
    {
        await _workers.StartAsync<StockPriceConsumer>(Fixture.BuildServices(
            ConnectionString,
            configure: s => s.AddScoped<IAlertEvaluationService>(sp =>
                new ProbingAlertEvaluationService(
                    new AlertEvaluationService(sp.GetRequiredService<AppDbContext>()),
                    _probe)),
            loggerProvider: _logs));

        // The consumer declares the retry/dead-letter topology; let it finish first.
        await Eventually.TrueAsync(
            async () => await _broker.ConsumerCountAsync(MainQueue) >= 1,
            PipelineTimeout,
            "consumer to subscribe");

        await _workers.StartAsync<AlertTriggeredConsumer>(Fixture.BuildServices(
            ConnectionString,
            configure: s => s.AddSingleton<IRealtimeNotifier>(_notifier),
            loggerProvider: _logs));

        await Eventually.TrueAsync(
            async () => await _broker.ConsumerCountAsync(QueueTopology.AlertTriggered.Queue) >= 1,
            PipelineTimeout,
            "notification consumer to subscribe");

        await _workers.StartAsync<OutboxPublisherWorker>(
            Fixture.BuildServices(ConnectionString, loggerProvider: _logs));
    }

    private async Task PollPricesAsync()
    {
        await using var db = NewDbContext();
        await new MarketDataService(db, _provider).UpdatePricesAsync();
    }

    private async Task<List<Guid>> PriceMessageIdsAsync()
    {
        await using var db = NewDbContext();
        return await db.OutboxMessages
            .Where(m => m.Type == nameof(StockPriceUpdatedMessage))
            .Select(m => m.Id)
            .ToListAsync();
    }

    private Task WaitUntilProcessedAsync(IReadOnlyCollection<Guid> messageIds) =>
        Eventually.TrueAsync(
            async () =>
            {
                await using var db = NewDbContext();
                return await db.ProcessedMessages.CountAsync(m => messageIds.Contains(m.Id))
                    == messageIds.Count;
            },
            PipelineTimeout,
            "every polled price to be consumed");

    private async Task<Guid> SeedAlertAsync(
        string symbol,
        AlertDirection direction,
        decimal targetPrice)
    {
        var alert = new Alert
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            Symbol = symbol,
            Direction = direction,
            TargetPrice = targetPrice
        };

        await using var db = NewDbContext();
        db.Alerts.Add(alert);
        await db.SaveChangesAsync();

        return alert.Id;
    }
}
