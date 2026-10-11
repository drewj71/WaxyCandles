using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WaxyCandles.AlertWorker;
using WaxyCandles.Application.Alerts.Interfaces;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure.Alerts;
using WaxyCandles.Infrastructure.MarketData;
using WaxyCandles.Infrastructure.Persistence;
using WaxyCandles.OutboxWorker;
using WaxyCandles.Tests.AlertWorker;
using WaxyCandles.Tests.Infrastructure;
using WaxyCandles.Tests.MarketData;
using static WaxyCandles.Tests.Infrastructure.RabbitMqTestBroker;

namespace WaxyCandles.Tests.Pipeline;

/// <summary>
/// End to end: price poll -> StockPrice + OutboxMessage -> OutboxWorker -> RabbitMQ
/// -> StockPriceConsumer -> alert triggered. Only the market data provider is faked.
/// </summary>
public class PricePipelineTests(IntegrationFixture fixture)
    : DatabaseTestBase(fixture)
{
    private static readonly TimeSpan PipelineTimeout = TimeSpan.FromSeconds(30);

    private readonly RunningWorkers _workers = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly EvaluationProbe _probe = new();
    private readonly FakeMarketDataProvider _provider = new();
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
    public async Task Polled_price_flows_through_the_outbox_and_triggers_the_alert()
    {
        var hit = await SeedAlertAsync("AAPL", AlertDirection.Above, 100m);
        var miss = await SeedAlertAsync("MSFT", AlertDirection.Below, 200m);
        _provider.SetQuote("AAPL", 150m);
        _provider.SetQuote("MSFT", 300m);

        await StartPipelineAsync();
        await PollPricesAsync();

        var messageIds = await OutboxIdsAsync();
        Assert.Equal(2, messageIds.Count);

        await WaitUntilProcessedAsync(messageIds);

        await using var db = NewDbContext();
        Assert.True((await db.Alerts.SingleAsync(a => a.Id == hit)).IsTriggered);
        Assert.False((await db.Alerts.SingleAsync(a => a.Id == miss)).IsTriggered);
        Assert.False(await db.OutboxMessages.AnyAsync(m => m.ProcessedAt == null));
        Assert.Equal(0u, await _broker.MessageCountAsync(DeadLetterQueue));
    }

    [Fact]
    public async Task Outbox_republish_after_a_crash_is_absorbed_by_the_idempotent_consumer()
    {
        await SeedAlertAsync("AAPL", AlertDirection.Above, 100m);
        _provider.SetQuote("AAPL", 150m);

        await StartPipelineAsync();
        await PollPricesAsync();

        var messageId = Assert.Single(await OutboxIdsAsync());
        await WaitUntilProcessedAsync([messageId]);

        // Simulate the outbox worker crashing after publishing but before saving
        // ProcessedAt: the row looks unpublished and gets sent again.
        await using (var db = NewDbContext())
        {
            var entry = await db.OutboxMessages.SingleAsync();
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

        await _workers.StartAsync<OutboxPublisherWorker>(
            Fixture.BuildServices(ConnectionString, loggerProvider: _logs));
    }

    private async Task PollPricesAsync()
    {
        await using var db = NewDbContext();
        await new MarketDataService(db, _provider).UpdatePricesAsync();
    }

    private async Task<List<Guid>> OutboxIdsAsync()
    {
        await using var db = NewDbContext();
        return await db.OutboxMessages.Select(m => m.Id).ToListAsync();
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
}
