using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WaxyCandles.AlertWorker;
using WaxyCandles.Application.Alerts.DTOs;
using WaxyCandles.Application.Alerts.Interfaces;
using WaxyCandles.Application.Alerts.Messages;
using WaxyCandles.Application.MarketData.Interfaces;
using WaxyCandles.Application.MarketData.Messages;
using WaxyCandles.Application.Notifications.Interfaces;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure;
using WaxyCandles.Infrastructure.Messaging;
using WaxyCandles.Infrastructure.Notifications;
using WaxyCandles.Infrastructure.Persistence;
using WaxyCandles.Tests.Infrastructure;
using WaxyCandles.Tests.MarketData;
using WaxyCandles.Tests.Notifications;
using static WaxyCandles.Tests.Resilience.FaultInjection;

namespace WaxyCandles.Tests.Resilience;

/// <summary>
/// REL-2: transient SQL errors are retried in-process by EF's execution
/// strategy, and every retried write stays exactly-once, including after an
/// ambiguous commit (committed, but the client saw an error).
/// </summary>
public class SqlRetryTests(IntegrationFixture fixture) : DatabaseTestBase(fixture)
{
    private readonly FakeMarketDataProvider _provider = new();
    private readonly RecordingNotifier _notifier = new();

    // ---- AlertWorker: claim + evaluation + outbox in one transaction ----

    [Fact]
    public Task Consumer_retries_a_transient_fault_on_the_claim() =>
        ConsumerRecoversFrom(new CommandFault(Sql("INSERT", "[ProcessedMessages]")), ConsumeOutcome.Processed);

    [Fact]
    public Task Consumer_retries_a_transient_fault_on_the_alert_update() =>
        ConsumerRecoversFrom(new CommandFault(Sql("UPDATE", "[Alerts]")), ConsumeOutcome.Processed);

    [Fact]
    public Task Consumer_retries_a_transient_fault_on_the_outbox_insert() =>
        ConsumerRecoversFrom(new CommandFault(Sql("INSERT", "[OutboxMessages]")), ConsumeOutcome.Processed);

    [Fact]
    public Task Consumer_retries_a_commit_that_failed() =>
        ConsumerRecoversFrom(new CommitFault(afterCommit: false), ConsumeOutcome.Processed);

    [Fact]
    public Task Consumer_retry_after_an_ambiguous_commit_finds_its_claim_and_fires_nothing_twice() =>
        // The first attempt committed; the retry finds the claim.
        ConsumerRecoversFrom(new CommitFault(afterCommit: true), ConsumeOutcome.AlreadyProcessed);

    private async Task ConsumerRecoversFrom(IInterceptor fault, ConsumeOutcome expected)
    {
        var alertId = await SeedAlertAsync();
        await using var services = Services(fault);
        var message = new StockPriceUpdatedMessage
        {
            MessageId = Guid.NewGuid(),
            Symbol = "AAPL",
            Price = 150m,
            Timestamp = DateTimeOffset.UtcNow
        };

        await using var scope = services.CreateAsyncScope();
        var outcome = await new TestStockPriceConsumer(services)
            .RunAsync(message, scope.ServiceProvider);

        Assert.Equal(expected, outcome);
        Assert.Equal(1, Thrown(fault));

        await using var db = NewDbContext();
        Assert.Equal(1, await db.ProcessedMessages.CountAsync(m => m.Id == message.MessageId));
        Assert.Equal(1, await db.OutboxMessages.CountAsync(m => m.Type == AlertTriggeredMessage.MessageType));

        var alert = await db.Alerts.SingleAsync(a => a.Id == alertId);
        Assert.True(alert.IsTriggered);
        Assert.Equal(1, alert.TriggerCount);
    }

    // ---- AlertEvaluationService without a caller transaction ----

    [Fact]
    public async Task Evaluation_in_its_own_transaction_retries_without_a_duplicate_event()
    {
        var alertId = await SeedAlertAsync();
        await using var services = Services(new CommandFault(Sql("INSERT", "[OutboxMessages]")));

        await EvaluateAsync(services);

        await AssertFiredOnceAsync(alertId);
    }

    [Fact]
    public async Task Evaluation_in_its_own_transaction_after_an_ambiguous_commit_fires_once()
    {
        var alertId = await SeedAlertAsync();
        var fault = new CommitFault(afterCommit: true);
        await using var services = Services(fault);

        await EvaluateAsync(services);

        Assert.Equal(1, fault.Thrown);
        await AssertFiredOnceAsync(alertId);
    }

    private static async Task EvaluateAsync(ServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider
            .GetRequiredService<IAlertEvaluationService>()
            .EvaluateAsync("AAPL", 150m, DateTimeOffset.UtcNow);
    }

    private async Task AssertFiredOnceAsync(Guid alertId)
    {
        await using var db = NewDbContext();
        Assert.Equal(1, await db.OutboxMessages.CountAsync(m => m.Type == AlertTriggeredMessage.MessageType));
        Assert.Equal(1, (await db.Alerts.SingleAsync(a => a.Id == alertId)).TriggerCount);
    }

    // ---- PricePollingWorker: StockPrice + OutboxMessage ----

    [Fact]
    public Task Poll_retries_a_transient_fault_on_the_insert() =>
        PollRecoversFrom(new CommandFault(Sql("INSERT", "[StockPrices]")));

    [Fact]
    public Task Poll_after_an_ambiguous_commit_succeeds_without_duplicates() =>
        PollRecoversFrom(new CommitFault(afterCommit: true));

    private async Task PollRecoversFrom(IInterceptor fault)
    {
        await SeedAlertAsync();
        _provider.SetQuote("AAPL", 150m);
        await using var services = Services(fault);

        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider
                .GetRequiredService<IMarketDataService>()
                .UpdatePricesAsync();
        }

        Assert.Equal(1, Thrown(fault));

        // Fetched once: the billed request is never part of the retried save.
        Assert.Single(_provider.QuoteRequests);

        await using var db = NewDbContext();
        Assert.Equal(1, await db.StockPrices.CountAsync());
        Assert.Equal(1, await db.OutboxMessages.CountAsync(m => m.Type == StockPriceUpdatedMessage.MessageType));
    }

    // ---- API: AlertTriggeredConsumer's notification ----

    [Fact]
    public async Task Notification_after_an_ambiguous_commit_is_created_and_pushed_once()
    {
        var userId = await SeedUserAsync();
        var fault = new CommitFault(afterCommit: true);
        await using var services = Services(fault);

        var message = new AlertTriggeredMessage
        {
            MessageId = Guid.NewGuid(),
            AlertId = Guid.NewGuid(),
            UserId = userId,
            Symbol = "AAPL",
            Direction = AlertDirection.Above,
            TargetPrice = 100m,
            TriggerPrice = 150m,
            TriggeredAt = DateTimeOffset.UtcNow
        };

        await using var scope = services.CreateAsyncScope();
        var outcome = await new TestAlertTriggeredConsumer(services)
            .RunAsync(message, scope.ServiceProvider);

        // Without the commit check the retry would hit the unique index,
        // report a duplicate and skip the push.
        Assert.Equal(1, fault.Thrown);
        Assert.Equal(ConsumeOutcome.Processed, outcome);
        Assert.Single(_notifier.Pushes);

        await using var db = NewDbContext();
        Assert.Equal(1, await db.Notifications.CountAsync(n => n.SourceMessageId == message.MessageId));
    }

    // ---- API: AlertService ----

    [Fact]
    public async Task Alert_create_after_an_ambiguous_commit_succeeds_with_one_alert()
    {
        var fault = new CommitFault(afterCommit: true);
        await using var services = Services(fault);

        await using (var scope = services.CreateAsyncScope())
        {
            var created = await scope.ServiceProvider
                .GetRequiredService<IAlertService>()
                .CreateAsync("user-1", new CreateAlertRequest
                {
                    Symbol = "AAPL",
                    TargetPrice = 100m,
                    Direction = AlertDirection.Above
                });

            Assert.Equal("AAPL", created.Symbol);
        }

        Assert.Equal(1, fault.Thrown);

        await using var db = NewDbContext();
        Assert.Equal(1, await db.Alerts.CountAsync());
    }

    [Fact]
    public async Task Alert_delete_retried_after_it_already_committed_succeeds()
    {
        var alertId = await SeedAlertAsync();

        // The delete runs (and, outside a transaction, commits), then the
        // client sees a transient error: the retry deletes nothing.
        var fault = new CommandFault(Sql("DELETE", "[Alerts]"), after: true);
        await using var services = Services(fault);

        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider
                .GetRequiredService<IAlertService>()
                .DeleteAsync(alertId, "user-1");
        }

        Assert.Equal(1, fault.Thrown);

        await using var db = NewDbContext();
        Assert.False(await db.Alerts.AnyAsync());
    }

    [Fact]
    public async Task Alert_delete_leaves_other_users_alerts_alone()
    {
        var alertId = await SeedAlertAsync();
        await using var services = Services();

        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider
                .GetRequiredService<IAlertService>()
                .DeleteAsync(alertId, "someone-else");
        }

        await using var db = NewDbContext();
        Assert.True(await db.Alerts.AnyAsync(a => a.Id == alertId));
    }

    // ---- Strategy configuration ----

    [Fact]
    public async Task App_context_uses_the_retrying_strategy()
    {
        await using var services = Services();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var strategy = db.Database.CreateExecutionStrategy();

        Assert.IsType<SqlServerRetryingExecutionStrategy>(strategy);
        Assert.True(strategy.RetriesOnFailure);
    }

    [Fact]
    public async Task A_transaction_outside_the_strategy_is_refused()
    {
        // The guard that makes a missed call site fail loudly in tests rather
        // than silently go unretried.
        await using var services = Services();
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await using var transaction = await db.Database.BeginTransactionAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Alerts.CountAsync());
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public async Task Default_retries_three_times(int faults, bool succeeds)
    {
        // Short delays, so the test doesn't wait out the real backoff.
        var fault = new CommandFault(Sql("[Alerts]"), times: faults);
        await using var services = Services(
            [fault],
            new() { ["Database:MaxRetryDelay"] = "00:00:00.050" });

        var count = CountAlertsAsync(services);

        if (succeeds)
        {
            Assert.Equal(0, await count);
        }
        else
        {
            var ex = await Assert.ThrowsAsync<RetryLimitExceededException>(() => count);
            Assert.IsType<SqlException>(ex.InnerException);
        }

        Assert.Equal(faults, fault.Thrown);
    }

    [Fact]
    public async Task Zero_retries_fails_on_the_first_fault()
    {
        var fault = new CommandFault(Sql("[Alerts]"), times: 1);
        await using var services = Services([fault], new() { ["Database:MaxRetryCount"] = "0" });

        await Assert.ThrowsAsync<RetryLimitExceededException>(() => CountAlertsAsync(services));
        Assert.Equal(1, fault.Thrown);
    }

    [Fact]
    public async Task Migrations_apply_through_the_retrying_context()
    {
        // The Docker migrations bundle builds its context from the app's DI.
        var connectionString = new SqlConnectionStringBuilder(ConnectionString)
        {
            InitialCatalog = $"WaxyCandlesTests_{Guid.NewGuid():N}"
        }.ConnectionString;

        await using var services = Fixture.BuildServices(connectionString);
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Database.MigrateAsync();

        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
    }

    private static async Task<int> CountAlertsAsync(ServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Alerts.CountAsync();
    }

    // ---- helpers ----

    private ServiceProvider Services(params IInterceptor[] interceptors) =>
        Services(interceptors, []);

    /// <summary>The app's real DI wiring, with fakes for the vendor and the push.</summary>
    private ServiceProvider Services(
        IInterceptor[] interceptors,
        Dictionary<string, string?> settings)
    {
        var all = Fixture.TestSettings(ConnectionString);

        foreach (var (key, value) in settings)
        {
            all[key] = value;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(all)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddInfrastructure(configuration);
        services.AddApiAuthentication(configuration);
        services.AddSingleton<IMarketDataProvider>(_provider);
        services.AddSingleton<IRealtimeNotifier>(_notifier);
        services.AddDbInterceptors(interceptors);

        return services.BuildServiceProvider(validateScopes: true);
    }

    private static int Thrown(IInterceptor fault) => fault switch
    {
        CommandFault c => c.Thrown,
        CommitFault c => c.Thrown,
        _ => throw new ArgumentOutOfRangeException(nameof(fault))
    };

    private async Task<Guid> SeedAlertAsync()
    {
        var alert = new Alert
        {
            Id = Guid.NewGuid(),
            UserId = "user-1",
            Symbol = "AAPL",
            Direction = AlertDirection.Above,
            TargetPrice = 100m
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

    private sealed class TestAlertTriggeredConsumer(IServiceProvider services)
        : AlertTriggeredConsumer(services, NullLogger<AlertTriggeredConsumer>.Instance)
    {
        public Task<ConsumeOutcome> RunAsync(AlertTriggeredMessage message, IServiceProvider scoped) =>
            ProcessAsync(message, scoped, CancellationToken.None);
    }
}
