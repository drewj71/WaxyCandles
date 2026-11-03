using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using WaxyCandles.Application.Alerts.Messages;
using WaxyCandles.Application.MarketData.Messages;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Infrastructure.Messaging;
using WaxyCandles.Infrastructure.Telemetry;
using WaxyCandles.OutboxWorker;
using WaxyCandles.Tests.Infrastructure;
using WaxyCandles.Tests.Telemetry;
using static WaxyCandles.Infrastructure.Telemetry.WaxyCandlesMetrics;

namespace WaxyCandles.Tests.OutboxWorker;

/// <summary>
/// The outbox worker's metrics against real SQL Server and RabbitMQ: published
/// count, backlog gauges, and queue depths read on a channel of their own.
/// </summary>
public class OutboxMetricsTests(IntegrationFixture fixture)
    : DatabaseTestBase(fixture)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // Two queue depth refreshes, plus slack.
    private static readonly TimeSpan QueueDepthTimeout =
        OutboxPublisherWorker.QueueDepthInterval * 2 + TimeSpan.FromSeconds(15);

    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 14, 30, 0, TimeSpan.Zero);

    private static readonly QueueTopology Prices = QueueTopology.StockPriceUpdated;
    private static readonly QueueTopology Alerts = QueueTopology.AlertTriggered;

    private readonly RunningWorkers _workers = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly MetricRecorder _metrics = new();
    private RabbitMqTestBroker _broker = default!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _broker = await RabbitMqTestBroker.ConnectAsync(Fixture);
        await _broker.DeleteTopologyAsync();

        // Process-wide state; don't let a previous test's values satisfy a wait.
        ClearQueueDepths();
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
    public async Task Published_messages_are_counted_by_queue_and_the_backlog_drains_to_zero()
    {
        await SeedAsync(StockPriceUpdatedMessage.MessageType, T0);
        await SeedAsync(StockPriceUpdatedMessage.MessageType, T0.AddSeconds(1));
        await SeedAsync(AlertTriggeredMessage.MessageType, T0.AddSeconds(2));

        await StartWorkerAsync();

        await Eventually.TrueAsync(
            async () =>
            {
                await using var db = NewDbContext();
                return !await db.OutboxMessages.AnyAsync(m => m.ProcessedAt == null);
            },
            Timeout,
            "all outbox messages to be published");

        Assert.Equal(2, _metrics.Sum("waxycandles.outbox.published", (QueueTag, Prices.Queue)));
        Assert.Equal(1, _metrics.Sum("waxycandles.outbox.published", (QueueTag, Alerts.Queue)));

        // The next poll sees nothing pending.
        await Eventually.TrueAsync(
            () => _metrics.Gauge("waxycandles.outbox.pending") == 0 &&
                  _metrics.Gauge("waxycandles.outbox.oldest_pending_age") == 0 &&
                  _metrics.Gauge("waxycandles.outbox.unroutable") == 0,
            Timeout,
            "the backlog gauges to read zero");
    }

    [Fact]
    public async Task Backlog_gauges_keep_updating_while_the_broker_is_down()
    {
        await SeedAsync(StockPriceUpdatedMessage.MessageType, T0);
        await SeedAsync(AlertTriggeredMessage.MessageType, T0.AddSeconds(30));
        await SeedAsync("SomethingNobodyRoutes", T0.AddDays(-1));

        await StartWorkerAsync(BrokerSettings(ClosedPort()));

        // Only routed rows count as pending; the unroutable row (older still)
        // doesn't set the age, and is reported on its own.
        await Eventually.TrueAsync(
            () => _metrics.Gauge("waxycandles.outbox.pending") == 2 &&
                  _metrics.Gauge("waxycandles.outbox.unroutable") == 1,
            Timeout,
            "the backlog gauges to report two pending rows and one unroutable");

        var expectedAge = (DateTimeOffset.UtcNow - T0).TotalSeconds;
        var age = _metrics.Gauge("waxycandles.outbox.oldest_pending_age");

        Assert.NotNull(age);
        Assert.InRange(age.Value, expectedAge - 30, expectedAge);
        Assert.Equal(2, CurrentOutboxBacklog!.Pending);

        // The backlog is read before the broker is tried, so the failure follows.
        await Eventually.TrueAsync(
            () => _logs.Contains("Error while publishing outbox messages."),
            Timeout,
            "the failed broker connection to be logged");

        Assert.Equal(0, _metrics.Sum("waxycandles.outbox.published"));
    }

    [Fact]
    public async Task Queue_depth_gauge_reports_main_retry_and_dead_letter_queues()
    {
        // A message parked in the dead-letter queue: the "something failed" signal.
        await using (var connection = await NewConnectionAsync())
        await using (var channel = await connection.CreateChannelAsync())
        {
            await Prices.DeclareAsync(channel);

            await channel.BasicPublishAsync(
                exchange: Prices.DeadLetterExchange,
                routingKey: QueueTopology.DeadLetterRoutingKey,
                mandatory: false,
                basicProperties: new BasicProperties { Persistent = true },
                body: Encoding.UTF8.GetBytes("{}"));
        }

        await StartWorkerAsync();

        await Eventually.TrueAsync(
            () => _metrics.Gauge("waxycandles.queue.messages", (QueueTag, Prices.DeadLetterQueue)) == 1,
            QueueDepthTimeout,
            "the dead-letter queue depth to be reported");

        Assert.Equal(0, _metrics.Gauge("waxycandles.queue.messages", (QueueTag, Prices.Queue)));
        Assert.Equal(0, _metrics.Gauge("waxycandles.queue.messages", (QueueTag, Prices.RetryQueue)));
        Assert.Equal(0, _metrics.Gauge("waxycandles.queue.messages", (QueueTag, Alerts.DeadLetterQueue)));

        // Main, retry and dead-letter for each route.
        Assert.Equal(6, _metrics.Observe("waxycandles.queue.messages").Count);
    }

    [Fact]
    public async Task Missing_queue_is_dropped_from_the_gauge_without_disturbing_publishing()
    {
        await StartWorkerAsync();

        await Eventually.TrueAsync(
            () => _metrics.Gauge("waxycandles.queue.messages", (QueueTag, Prices.RetryQueue)) == 0,
            QueueDepthTimeout,
            "the first queue depth refresh");

        // The passive declare of this queue now fails with a 404, which closes
        // the channel it ran on. It's second of six, so four more follow it.
        await _broker.DeleteQueueAsync(Prices.RetryQueue);

        await Eventually.TrueAsync(
            () => _metrics.Gauge("waxycandles.queue.messages", (QueueTag, Prices.RetryQueue)) is null,
            QueueDepthTimeout,
            "the missing queue to stop being reported");

        // The queues checked after it in the same refresh are still reported:
        // the closed channel was replaced.
        Assert.Equal(0, _metrics.Gauge("waxycandles.queue.messages", (QueueTag, Prices.DeadLetterQueue)));
        Assert.Equal(5, _metrics.Observe("waxycandles.queue.messages").Count);

        // Publishing, on its own channel, is unaffected.
        var entry = await SeedAsync(StockPriceUpdatedMessage.MessageType, T0);

        await Eventually.TrueAsync(
            async () =>
            {
                await using var db = NewDbContext();
                return await db.OutboxMessages.AnyAsync(m => m.Id == entry.Id && m.ProcessedAt != null);
            },
            Timeout,
            "the outbox row to be published");

        Assert.False(_logs.Contains("Error while publishing outbox messages."));
        Assert.False(_logs.Contains("RabbitMQ channel was closed"));
        Assert.Equal(1, _metrics.Sum("waxycandles.outbox.published", (QueueTag, Prices.Queue)));
    }

    private Task StartWorkerAsync() =>
        _workers.StartAsync<OutboxPublisherWorker>(
            Fixture.BuildServices(ConnectionString, loggerProvider: _logs));

    private Task StartWorkerAsync(IConfiguration configuration) =>
        _workers.StartAsync<OutboxPublisherWorker>(
            Fixture.BuildServices(
                ConnectionString,
                services => services.AddSingleton(configuration),
                loggerProvider: _logs));

    private Task<IConnection> NewConnectionAsync() =>
        new ConnectionFactory
        {
            HostName = Fixture.RabbitMqHost,
            Port = Fixture.RabbitMqPort
        }.CreateConnectionAsync();

    private async Task<OutboxMessage> SeedAsync(string type, DateTimeOffset createdAt)
    {
        var entry = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = type,
            Payload = "{}",
            CreatedAt = createdAt
        };

        await using var db = NewDbContext();
        db.OutboxMessages.Add(entry);
        await db.SaveChangesAsync();

        return entry;
    }

    private IConfigurationRoot BrokerSettings(int port)
    {
        var settings = Fixture.TestSettings(ConnectionString);
        settings["RabbitMQ:Port"] = port.ToString();

        return new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
    }

    // A local port nothing listens on, so connecting is refused straight away.
    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }
}
