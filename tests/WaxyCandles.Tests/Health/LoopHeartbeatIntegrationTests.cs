using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using WaxyCandles.AlertWorker;
using WaxyCandles.Application.Alerts.Interfaces;
using WaxyCandles.Application.MarketData.Messages;
using WaxyCandles.Infrastructure.Alerts;
using WaxyCandles.Infrastructure.Health;
using WaxyCandles.Infrastructure.Messaging;
using WaxyCandles.Infrastructure.Persistence;
using WaxyCandles.OutboxWorker;
using WaxyCandles.Tests.AlertWorker;
using WaxyCandles.Tests.Infrastructure;
using WaxyCandles.Tests.Resilience;
using static WaxyCandles.Tests.Infrastructure.RabbitMqTestBroker;
using static WaxyCandles.Tests.Resilience.FaultInjection;

namespace WaxyCandles.Tests.Health;

/// <summary>
/// The loops' explicit heartbeats against real SQL Server and RabbitMQ: the
/// outbox loop beats while the broker is down but not while the database is
/// unreadable, and the AlertWorker's consumer reports only the delivery it is
/// processing, so an idle queue or a broker outage never looks stuck.
/// </summary>
public class LoopHeartbeatIntegrationTests(IntegrationFixture fixture)
    : DatabaseTestBase(fixture)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

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

    // ---- OutboxWorker ----

    [Fact]
    public async Task Outbox_loop_keeps_beating_while_the_broker_is_down()
    {
        var heartbeat = new OutboxLoopHeartbeat(TimeProvider.System);
        var settings = Fixture.TestSettings(ConnectionString);
        settings["RabbitMQ:Port"] = ClosedPort().ToString();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        await _workers.StartAsync<OutboxPublisherWorker>(
            Fixture.BuildServices(
                ConnectionString,
                s =>
                {
                    s.AddSingleton<IConfiguration>(configuration);
                    s.AddSingleton(heartbeat);
                },
                loggerProvider: _logs));

        await Eventually.TrueAsync(
            () => _logs.Contains("Error while publishing outbox messages."),
            Timeout,
            "the failed broker connection to be logged");

        var first = heartbeat.LastBeatAt;
        Assert.NotNull(first);

        // Still iterating (every 5 s) with the broker unreachable.
        await Eventually.TrueAsync(
            () => heartbeat.LastBeatAt > first,
            Timeout,
            "another beat while the broker is down");
    }

    [Fact]
    public async Task Outbox_loop_stops_beating_while_the_database_is_unreadable()
    {
        var heartbeat = new OutboxLoopHeartbeat(TimeProvider.System);
        var fault = new CommandFault(Sql("COUNT", "[OutboxMessages]"), times: int.MaxValue);

        await _workers.StartAsync<OutboxPublisherWorker>(
            Fixture.BuildServices(
                ConnectionString,
                s =>
                {
                    s.AddSingleton(heartbeat);
                    s.AddDbInterceptors(fault);
                },
                loggerProvider: _logs));

        await Eventually.TrueAsync(
            () => _logs.Contains("Error while reading the outbox backlog."),
            Timeout,
            "the failed backlog read to be logged");

        Assert.Null(heartbeat.LastBeatAt);

        // The database is back: the loop beats again.
        fault.Armed = false;

        await Eventually.TrueAsync(
            () => heartbeat.LastBeatAt is not null,
            Timeout,
            "a beat once the database is readable");
    }

    // ---- AlertWorker ----

    [Fact]
    public async Task Consumer_reports_the_delivery_it_is_processing_and_clears_it_when_done()
    {
        var tracker = new ConsumerProcessingTracker(TimeProvider.System);
        var release = new TaskCompletionSource();
        var probe = new EvaluationProbe { BeforeEvaluate = _ => release.Task };

        await StartConsumerAsync(tracker, probe);

        // Idle: subscribed, nothing in flight.
        Assert.Null(tracker.OldestInFlight());

        var message = NewMessage();
        await _broker.PublishAsync(message);

        await Eventually.TrueAsync(
            () => tracker.OldestInFlight()?.Queue == MainQueue,
            Timeout,
            "the delivery to be reported as processing");

        var check = new ConsumerStallHealthCheck(tracker, TimeSpan.FromMinutes(5), TimeProvider.System);
        Assert.Equal(HealthStatus.Healthy, (await RunAsync(check)).Status);

        release.SetResult();
        await WaitUntilProcessedAsync(message.MessageId);

        await Eventually.TrueAsync(
            () => tracker.OldestInFlight() is null,
            Timeout,
            "the finished delivery to be cleared");
    }

    [Fact]
    public async Task Delivery_in_processing_during_a_connection_drop_is_cleared_and_not_reported_as_stuck()
    {
        var tracker = new ConsumerProcessingTracker(TimeProvider.System);
        var release = new TaskCompletionSource();
        var probe = new EvaluationProbe
        {
            BeforeEvaluate = attempt => attempt == 1 ? release.Task : Task.CompletedTask
        };

        await StartConsumerAsync(tracker, probe);

        var message = NewMessage();
        await _broker.PublishAsync(message);

        await Eventually.TrueAsync(
            () => tracker.OldestInFlight() is not null,
            Timeout,
            "the delivery to be reported as processing");

        // The broker connection drops mid-processing. The processing still
        // finishes; its ack fails, and broker waits are never tracked.
        await Fixture.CloseAllRabbitMqConnectionsAsync();
        release.SetResult();

        await Eventually.TrueAsync(
            () => tracker.OldestInFlight() is null,
            Timeout,
            "the delivery to be cleared once its processing ended");

        // Committed before the failed ack; the redelivery after recovery is
        // absorbed by the idempotent consumer.
        await WaitUntilProcessedAsync(message.MessageId);

        // The test's own connection was closed too: let it recover (and the
        // consumer re-register) before cleanup uses it.
        await Eventually.TrueAsync(
            async () => await TryGetAsync(() => _broker.ConsumerCountAsync(MainQueue)) == 1,
            Timeout,
            "automatic recovery to re-register the consumer");

        Assert.Null(tracker.OldestInFlight());
    }

    private static async Task<uint?> TryGetAsync(Func<Task<uint>> probe)
    {
        try
        {
            return await probe();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- helpers ----

    private async Task StartConsumerAsync(ConsumerProcessingTracker tracker, EvaluationProbe probe)
    {
        var services = Fixture.BuildServices(
            ConnectionString,
            configure: s =>
            {
                s.AddSingleton(tracker);
                s.AddScoped<IAlertEvaluationService>(sp =>
                    new ProbingAlertEvaluationService(
                        new AlertEvaluationService(sp.GetRequiredService<AppDbContext>()),
                        probe));
            },
            loggerProvider: _logs);

        await _workers.StartAsync<StockPriceConsumer>(services);

        await Eventually.TrueAsync(
            async () => await _broker.ConsumerCountAsync(MainQueue) >= 1,
            Timeout,
            $"a consumer to subscribe to {MainQueue}");
    }

    private Task WaitUntilProcessedAsync(Guid messageId) =>
        Eventually.TrueAsync(
            async () =>
            {
                await using var db = NewDbContext();
                return await db.ProcessedMessages.AnyAsync(m => m.Id == messageId);
            },
            Timeout,
            $"message {messageId} to be recorded as processed");

    private static Task<HealthCheckResult> RunAsync(IHealthCheck check) =>
        check.CheckHealthAsync(new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("test", check, null, null)
        });

    private static StockPriceUpdatedMessage NewMessage() => new()
    {
        MessageId = Guid.NewGuid(),
        Symbol = "AAPL",
        Price = 150m,
        Timestamp = DateTimeOffset.UtcNow
    };

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
