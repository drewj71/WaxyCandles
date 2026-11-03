using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using WaxyCandles.Application.MarketData.Messages;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Infrastructure.Retention;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Retention;

/// <summary>
/// DataRetentionWorker against real SQL Server, with a fixed clock for cutoffs.
/// Defaults: processed outbox rows and idempotency claims 7 days, prices 30 days.
/// </summary>
public class RetentionTests(IntegrationFixture fixture)
    : DatabaseTestBase(fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly CapturingLoggerProvider _logs = new();
    private ServiceProvider? _services;

    public override async Task DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    [Fact]
    public async Task Only_processed_outbox_rows_past_retention_are_deleted_and_pending_rows_survive_any_age()
    {
        var pendingAncient = Outbox(createdAt: Now.AddYears(-5), processedAt: null);
        var pendingFresh = Outbox(createdAt: Now.AddMinutes(-1), processedAt: null);
        var processedOld = Outbox(createdAt: Now.AddDays(-9), processedAt: Now.AddDays(-8));
        var processedFresh = Outbox(createdAt: Now.AddDays(-9), processedAt: Now.AddDays(-6));

        await SeedAsync(db => db.OutboxMessages.AddRange(pendingAncient, pendingFresh, processedOld, processedFresh));

        var result = await Worker().RunPassAsync(CancellationToken.None);

        Assert.Equal(1, result.OutboxMessages);

        await using var db = NewDbContext();
        Assert.Equal(
            new[] { pendingAncient.Id, pendingFresh.Id, processedFresh.Id }.Order(),
            (await db.OutboxMessages.Select(x => x.Id).ToListAsync()).Order());
    }

    [Fact]
    public async Task Only_idempotency_claims_past_retention_are_deleted()
    {
        var old = new ProcessedMessage { Id = Guid.NewGuid(), ProcessedAt = Now.AddDays(-8) };
        var fresh = new ProcessedMessage { Id = Guid.NewGuid(), ProcessedAt = Now.AddDays(-6) };

        await SeedAsync(db => db.ProcessedMessages.AddRange(old, fresh));

        var result = await Worker().RunPassAsync(CancellationToken.None);

        Assert.Equal(1, result.ProcessedMessages);

        await using var db = NewDbContext();
        Assert.Equal([fresh.Id], await db.ProcessedMessages.Select(x => x.Id).ToListAsync());
    }

    [Fact]
    public async Task Old_prices_are_deleted_but_each_symbols_latest_price_always_survives()
    {
        // AAPL: old rows and a fresh one. Only the fresh one stays.
        var aaplOld1 = Price("AAPL", Now.AddDays(-40));
        var aaplOld2 = Price("AAPL", Now.AddDays(-31));
        var aaplFresh = Price("AAPL", Now.AddDays(-1));

        // MSFT: every row is old. The newest of them is still its latest price.
        var msftOldest = Price("MSFT", Now.AddDays(-60));
        var msftLatest = Price("MSFT", Now.AddDays(-45));

        // TSLA: a single, very old row.
        var tslaOnly = Price("TSLA", Now.AddDays(-365));

        // NVDA: nothing old.
        var nvdaFresh1 = Price("NVDA", Now.AddDays(-29));
        var nvdaFresh2 = Price("NVDA", Now.AddDays(-2));

        await SeedAsync(db => db.StockPrices.AddRange(
            aaplOld1, aaplOld2, aaplFresh, msftOldest, msftLatest, tslaOnly, nvdaFresh1, nvdaFresh2));

        var result = await Worker().RunPassAsync(CancellationToken.None);

        Assert.Equal(3, result.StockPrices);

        await using var db = NewDbContext();
        Assert.Equal(
            new[] { aaplFresh.Id, msftLatest.Id, tslaOnly.Id, nvdaFresh1.Id, nvdaFresh2.Id }.Order(),
            (await db.StockPrices.Select(x => x.Id).ToListAsync()).Order());
    }

    [Theory]
    [InlineData(25)] // batches of 10, 10, 5
    [InlineData(20)] // batches of 10, 10, 0
    public async Task More_eligible_rows_than_one_batch_are_all_deleted(int count)
    {
        await SeedAsync(db =>
        {
            for (var i = 0; i < count; i++)
            {
                db.OutboxMessages.Add(Outbox(Now.AddDays(-10), Now.AddDays(-9).AddSeconds(i)));
                db.ProcessedMessages.Add(new ProcessedMessage { Id = Guid.NewGuid(), ProcessedAt = Now.AddDays(-9).AddSeconds(i) });
                db.StockPrices.Add(Price("AAPL", Now.AddDays(-40).AddSeconds(i)));
            }

            db.OutboxMessages.Add(Outbox(Now.AddDays(-10), processedAt: null));
            db.StockPrices.Add(Price("AAPL", Now.AddMinutes(-1)));
        });

        var result = await Worker(batchSize: 10).RunPassAsync(CancellationToken.None);

        Assert.Equal(new RetentionPassResult(count, count, count), result);

        await using var db = NewDbContext();
        Assert.Equal(1, await db.OutboxMessages.CountAsync());
        Assert.Equal(0, await db.ProcessedMessages.CountAsync());
        Assert.Equal(1, await db.StockPrices.CountAsync());
    }

    [Fact]
    public async Task A_second_pass_deletes_nothing()
    {
        await SeedAsync(db =>
        {
            db.OutboxMessages.Add(Outbox(Now.AddDays(-10), Now.AddDays(-9)));
            db.OutboxMessages.Add(Outbox(Now.AddDays(-10), processedAt: null));
            db.ProcessedMessages.Add(new ProcessedMessage { Id = Guid.NewGuid(), ProcessedAt = Now.AddDays(-9) });
            db.StockPrices.Add(Price("AAPL", Now.AddDays(-50)));
            db.StockPrices.Add(Price("AAPL", Now.AddDays(-40)));
        });

        var worker = Worker();

        Assert.Equal(new RetentionPassResult(1, 1, 1), await worker.RunPassAsync(CancellationToken.None));
        Assert.Equal(new RetentionPassResult(0, 0, 0), await worker.RunPassAsync(CancellationToken.None));

        await using var db = NewDbContext();
        Assert.Equal(1, await db.OutboxMessages.CountAsync());
        Assert.Equal(1, await db.StockPrices.CountAsync());
    }

    [Fact]
    public async Task A_database_error_is_logged_does_not_skip_other_tables_and_does_not_stop_the_service()
    {
        var oldClaim = new ProcessedMessage { Id = Guid.NewGuid(), ProcessedAt = Now.AddDays(-9) };

        await SeedAsync(db =>
        {
            db.ProcessedMessages.Add(oldClaim);
            db.OutboxMessages.Add(Outbox(Now.AddDays(-10), Now.AddDays(-9)));
        });

        // Makes every ProcessedMessages delete fail with "Invalid object name".
        await using (var db = NewDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("EXEC sp_rename 'dbo.ProcessedMessages', 'ProcessedMessagesHidden'");
        }

        BuildServices(interval: TimeSpan.FromMilliseconds(200));
        var hosted = _services!.GetRequiredService<IHostedService>();
        await hosted.StartAsync(CancellationToken.None);

        try
        {
            await Eventually.TrueAsync(
                () => _logs.Contains("Retention failed for ProcessedMessages"),
                TimeSpan.FromSeconds(30),
                "the ProcessedMessages failure to be logged");

            // The other tables were still purged.
            await Eventually.TrueAsync(
                async () => { await using var db = NewDbContext(); return !await db.OutboxMessages.AnyAsync(); },
                TimeSpan.FromSeconds(30),
                "the processed outbox row to be deleted despite the other table failing");

            await using (var db = NewDbContext())
            {
                await db.Database.ExecuteSqlRawAsync("EXEC sp_rename 'dbo.ProcessedMessagesHidden', 'ProcessedMessages'");
            }

            // Still running: a later pass deletes the claim.
            await Eventually.TrueAsync(
                async () => { await using var db = NewDbContext(); return !await db.ProcessedMessages.AnyAsync(); },
                TimeSpan.FromSeconds(30),
                "a later pass to delete the old claim once the table is back");
        }
        finally
        {
            await hosted.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("Retention:Interval", "00:00:00")]
    [InlineData("Retention:Interval", "31.00:00:00")]
    [InlineData("Retention:InitialDelay", "-00:00:01")]
    [InlineData("Retention:InitialDelay", "50.00:00:00")]
    [InlineData("Retention:OutboxMessageRetention", "00:00:00")]
    [InlineData("Retention:ProcessedMessageRetention", "00:00:00")]
    [InlineData("Retention:ProcessedMessageRetention", "3651.00:00:00")]
    [InlineData("Retention:StockPriceRetention", "-1.00:00:00")]
    [InlineData("Retention:StockPriceRetention", "100000.00:00:00")]
    [InlineData("Retention:BatchSize", "0")]
    [InlineData("Retention:BatchSize", "4001")]
    public void Invalid_options_fail_validation(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        using var services = new ServiceCollection()
            .AddDataRetention(configuration)
            .BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => services.GetRequiredService<IOptions<RetentionOptions>>().Value);

        Assert.Contains(key, ex.Message);
    }

    [Fact]
    public void Defaults_are_the_agreed_retention_periods()
    {
        using var services = new ServiceCollection()
            .AddDataRetention(new ConfigurationBuilder().Build())
            .BuildServiceProvider();

        var options = services.GetRequiredService<IOptions<RetentionOptions>>().Value;

        Assert.Equal(TimeSpan.FromHours(1), options.Interval);
        Assert.Equal(TimeSpan.FromDays(7), options.OutboxMessageRetention);
        Assert.Equal(TimeSpan.FromDays(7), options.ProcessedMessageRetention);
        Assert.Equal(TimeSpan.FromDays(30), options.StockPriceRetention);
        Assert.Equal(1000, options.BatchSize);
    }

    private DataRetentionWorker Worker(int? batchSize = null)
    {
        BuildServices(batchSize: batchSize);
        return (DataRetentionWorker)_services!.GetRequiredService<IHostedService>();
    }

    private void BuildServices(int? batchSize = null, TimeSpan? interval = null)
    {
        _services = Fixture.BuildServices(
            ConnectionString,
            services =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["Retention:InitialDelay"] = "00:00:00"
                };

                if (batchSize is not null)
                {
                    settings["Retention:BatchSize"] = batchSize.ToString();
                }

                if (interval is not null)
                {
                    settings["Retention:Interval"] = interval.Value.ToString();
                }

                services.AddSingleton<TimeProvider>(new FixedClock(Now));
                services.AddDataRetention(
                    new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
                services.AddHostedService<DataRetentionWorker>();
            },
            _logs);
    }

    private async Task SeedAsync(Action<WaxyCandles.Infrastructure.Persistence.AppDbContext> seed)
    {
        await using var db = NewDbContext();
        seed(db);
        await db.SaveChangesAsync();
    }

    private static OutboxMessage Outbox(DateTimeOffset createdAt, DateTimeOffset? processedAt) => new()
    {
        Id = Guid.NewGuid(),
        Type = StockPriceUpdatedMessage.MessageType,
        Payload = "{}",
        CreatedAt = createdAt,
        ProcessedAt = processedAt
    };

    private static StockPrice Price(string symbol, DateTimeOffset timestamp) => new()
    {
        Id = Guid.NewGuid(),
        Symbol = symbol,
        Price = 100m,
        Timestamp = timestamp
    };

    /// <summary>Fixed wall clock for cutoffs; timers (Task.Delay) still run in real time.</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
