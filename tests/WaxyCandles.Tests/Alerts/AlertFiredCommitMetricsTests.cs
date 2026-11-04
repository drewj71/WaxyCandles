using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WaxyCandles.AlertWorker;
using WaxyCandles.Application.Alerts.Interfaces;
using WaxyCandles.Application.MarketData.Messages;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure;
using WaxyCandles.Infrastructure.Alerts;
using WaxyCandles.Infrastructure.Messaging;
using WaxyCandles.Infrastructure.Persistence;
using WaxyCandles.Tests.AlertWorker;
using WaxyCandles.Tests.Infrastructure;
using WaxyCandles.Tests.Resilience;
using WaxyCandles.Tests.Telemetry;
using static WaxyCandles.Infrastructure.Telemetry.WaxyCandlesMetrics;
using static WaxyCandles.Tests.Resilience.FaultInjection;

namespace WaxyCandles.Tests.Alerts;

/// <summary>
/// waxycandles.alerts.fired counts committed fires only: the AlertWorker
/// records after its commit, so a rolled-back or replayed attempt is never
/// counted. Real SQL Server, the real consumer's processing and real
/// transient SqlExceptions.
/// </summary>
public class AlertFiredCommitMetricsTests(IntegrationFixture fixture)
    : DatabaseTestBase(fixture)
{
    private const string Fired = "waxycandles.alerts.fired";

    private readonly MetricRecorder _metrics = new();

    public override Task DisposeAsync()
    {
        _metrics.Dispose();
        return base.DisposeAsync();
    }

    [Fact]
    public async Task Consumer_commit_that_fails_and_is_replayed_counts_the_fire_once()
    {
        await SeedAlertAsync();
        var fault = new CommitFault(afterCommit: false);
        await using var services = Services([fault]);

        var outcome = await RunConsumerAsync(services, NewMessage());

        Assert.Equal(ConsumeOutcome.Processed, outcome);
        Assert.Equal(1, fault.Thrown);

        // The first attempt fired the alert in its transaction, then rolled back.
        Assert.Equal(1, _metrics.Sum(Fired, (KindTag, "price")));
    }

    [Fact]
    public async Task Consumer_transient_fault_inside_evaluation_counts_the_fire_once()
    {
        await SeedAlertAsync();
        var fault = new CommandFault(Sql("INSERT", "[OutboxMessages]"));
        await using var services = Services([fault]);

        await RunConsumerAsync(services, NewMessage());

        Assert.Equal(1, fault.Thrown);
        Assert.Equal(1, _metrics.Sum(Fired));
    }

    [Fact]
    public async Task Rollback_then_queue_retry_counts_the_fire_once()
    {
        await SeedAlertAsync();
        var probe = new EvaluationProbe
        {
            // Not transient: the strategy doesn't retry, the attempt rolls back
            // and the delivery goes back through the queue.
            AfterEvaluate = attempt => attempt == 1
                ? throw new InvalidOperationException("Simulated failure before the commit")
                : Task.CompletedTask
        };
        await using var services = Services([], probe);
        var message = NewMessage();

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunConsumerAsync(services, message));
        Assert.Equal(0, _metrics.Sum(Fired));

        // The queue retry: same message, a new scope.
        Assert.Equal(ConsumeOutcome.Processed, await RunConsumerAsync(services, message));
        Assert.Equal(1, _metrics.Sum(Fired));

        // A redelivery after that adds nothing.
        Assert.Equal(ConsumeOutcome.AlreadyProcessed, await RunConsumerAsync(services, message));
        Assert.Equal(1, _metrics.Sum(Fired));
    }

    [Fact]
    public async Task Ambiguous_commit_never_over_counts()
    {
        await SeedAlertAsync();
        var fault = new CommitFault(afterCommit: true);
        await using var services = Services([fault]);

        var outcome = await RunConsumerAsync(services, NewMessage());

        // The first attempt committed, but its result was lost with the
        // error; the replay finds the claim and fires nothing. A known,
        // rare under-count rather than a double count.
        Assert.Equal(ConsumeOutcome.AlreadyProcessed, outcome);
        Assert.Equal(0, _metrics.Sum(Fired));
    }

    [Fact]
    public async Task Evaluation_in_its_own_transaction_counts_a_replayed_commit_once()
    {
        await SeedAlertAsync(level: GexLevelKind.King);
        await using var services = Services([new CommitFault(afterCommit: false)]);

        await using var scope = services.CreateAsyncScope();
        var result = await scope.ServiceProvider
            .GetRequiredService<IAlertEvaluationService>()
            .EvaluateAsync("AAPL", 150m, DateTimeOffset.UtcNow);

        var fired = Assert.Single(result.Fired);
        Assert.Equal(GexLevelKind.King, fired.Level);
        Assert.Equal(1, _metrics.Sum(Fired, (KindTag, "level")));
        Assert.Equal(1, _metrics.Sum(Fired));
    }

    [Fact]
    public async Task Evaluation_inside_a_caller_transaction_returns_the_fires_without_counting_them()
    {
        var alertId = await SeedAlertAsync();
        await using var services = Services([]);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var result = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var evaluation = await scope.ServiceProvider
                .GetRequiredService<IAlertEvaluationService>()
                .EvaluateAsync("AAPL", 150m, DateTimeOffset.UtcNow);
            await transaction.RollbackAsync();
            return evaluation;
        });

        Assert.Equal(alertId, Assert.Single(result.Fired).AlertId);

        // The caller owns the commit and rolled back: nothing to count.
        Assert.Equal(0, _metrics.Sum(Fired));
    }

    private static async Task<ConsumeOutcome> RunConsumerAsync(
        ServiceProvider services,
        StockPriceUpdatedMessage message)
    {
        await using var scope = services.CreateAsyncScope();
        return await new TestStockPriceConsumer(services).RunAsync(message, scope.ServiceProvider);
    }

    private static StockPriceUpdatedMessage NewMessage() => new()
    {
        MessageId = Guid.NewGuid(),
        Symbol = "AAPL",
        Price = 150m,
        Timestamp = DateTimeOffset.UtcNow
    };

    /// <summary>The app's real DI wiring, with fault interceptors and an optional probe.</summary>
    private ServiceProvider Services(IInterceptor[] interceptors, EvaluationProbe? probe = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(Fixture.TestSettings(ConnectionString))
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddInfrastructure(configuration);
        services.AddDbInterceptors(interceptors);

        if (probe is not null)
        {
            services.AddScoped<IAlertEvaluationService>(sp =>
                new ProbingAlertEvaluationService(
                    new AlertEvaluationService(sp.GetRequiredService<AppDbContext>()),
                    probe));
        }

        return services.BuildServiceProvider(validateScopes: true);
    }

    private async Task<Guid> SeedAlertAsync(GexLevelKind? level = null)
    {
        var alert = new Alert
        {
            Id = Guid.NewGuid(),
            UserId = "user-1",
            Symbol = "AAPL",
            Direction = AlertDirection.Above,
            TargetPrice = 100m,
            Level = level,
            LevelPrice = level is null ? null : 101m,
            LevelProximityPercent = level is null ? null : Alert.DefaultLevelProximityPercent,
            LevelDays = level is null ? null : Alert.DefaultLevelDays,
            LevelStrikeRange = level is null ? null : Alert.DefaultLevelStrikeRange
        };

        await using var db = NewDbContext();
        db.Alerts.Add(alert);
        await db.SaveChangesAsync();

        return alert.Id;
    }

    private sealed class TestStockPriceConsumer(IServiceProvider services)
        : StockPriceConsumer(services, NullLogger<StockPriceConsumer>.Instance)
    {
        public Task<ConsumeOutcome> RunAsync(StockPriceUpdatedMessage message, IServiceProvider scoped) =>
            ProcessAsync(message, scoped, CancellationToken.None);
    }
}
