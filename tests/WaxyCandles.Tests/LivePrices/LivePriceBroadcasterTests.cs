using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WaxyCandles.Application.MarketData.LivePrices;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Infrastructure.LivePrices;
using WaxyCandles.Infrastructure.Persistence;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.LivePrices;

/// <summary>
/// The live price loop's passes against a real database: only prices newer
/// than the last push go out, nothing is queried while nobody watches, and
/// one query covers every watched symbol.
/// </summary>
public class LivePriceBroadcasterTests(IntegrationFixture fixture)
    : DatabaseTestBase(fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 15, 0, 0, TimeSpan.Zero);

    private readonly LivePriceSubscriptions _subscriptions = new();
    private readonly RecordingLivePriceNotifier _notifier = new();
    private readonly CommandCounter _commands = new();
    private ServiceProvider _services = default!;
    private LivePriceBroadcaster _broadcaster = default!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options
            .UseSqlServer(ConnectionString, sql => sql.EnableRetryOnFailure())
            .AddInterceptors(_commands));
        _services = services.BuildServiceProvider();

        _broadcaster = new LivePriceBroadcaster(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _subscriptions,
            _notifier,
            Microsoft.Extensions.Options.Options.Create(new LivePricesOptions()),
            TimeProvider.System,
            NullLogger<LivePriceBroadcaster>.Instance);
    }

    public override async Task DisposeAsync()
    {
        _broadcaster.Dispose();
        await _services.DisposeAsync();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Nothing_is_queried_while_no_one_is_subscribed()
    {
        await SeedPriceAsync("AAPL", 100m, T0);

        Assert.Equal(0, await _broadcaster.RunOnceAsync());
        Assert.Equal(0, _commands.Count);

        // Subscribed and then gone again: back to no queries.
        _subscriptions.Subscribe("c1", "AAPL", 10);
        await _broadcaster.RunOnceAsync();
        Assert.Equal(1, _commands.Count);

        _subscriptions.RemoveConnection("c1");
        await _broadcaster.RunOnceAsync();
        await _broadcaster.RunOnceAsync();
        Assert.Equal(1, _commands.Count);
    }

    [Fact]
    public async Task Only_prices_newer_than_the_last_push_are_pushed()
    {
        _subscriptions.Subscribe("c1", "AAPL", 10);
        _subscriptions.Subscribe("c1", "MSFT", 10);
        _subscriptions.Subscribe("c2", "AAPL", 10);

        await SeedPriceAsync("AAPL", 100m, T0, change: 2m, changePercent: 2.04m);

        // First pass: the current price, once, however many clients watch it.
        Assert.Equal(1, await _broadcaster.RunOnceAsync());
        var first = Assert.Single(_notifier.Pushed);
        Assert.Equal(new LivePriceUpdate("AAPL", 100m, 2m, 2.04m, 98m, T0), first);

        // Nothing new: nothing pushed.
        Assert.Equal(0, await _broadcaster.RunOnceAsync());

        // A newer AAPL and a first MSFT.
        await SeedPriceAsync("AAPL", 101m, T0.AddMinutes(1));
        await SeedPriceAsync("MSFT", 300m, T0.AddMinutes(1));
        Assert.Equal(2, await _broadcaster.RunOnceAsync());

        // A late-arriving older price is not newer than what was pushed.
        await SeedPriceAsync("AAPL", 95m, T0.AddSeconds(30));
        Assert.Equal(0, await _broadcaster.RunOnceAsync());

        Assert.Equal(
            [("AAPL", 100m), ("AAPL", 101m), ("MSFT", 300m)],
            _notifier.Pushed.Select(x => (x.Symbol, x.Price)).Order().ToArray());

        // One query per pass, whatever the number of symbols.
        Assert.Equal(4, _commands.Count);
    }

    [Fact]
    public async Task A_symbol_watched_again_pushes_its_current_price_once()
    {
        await SeedPriceAsync("AAPL", 100m, T0);

        _subscriptions.Subscribe("c1", "AAPL", 10);
        await _broadcaster.RunOnceAsync();

        _subscriptions.Unsubscribe("c1", "AAPL");
        await _broadcaster.RunOnceAsync();

        _subscriptions.Subscribe("c2", "AAPL", 10);
        Assert.Equal(1, await _broadcaster.RunOnceAsync());
        Assert.Equal(2, _notifier.Pushed.Count);
    }

    [Fact]
    public async Task A_symbol_with_no_stored_price_pushes_nothing()
    {
        _subscriptions.Subscribe("c1", "NOPE", 10);

        Assert.Equal(0, await _broadcaster.RunOnceAsync());
        Assert.Empty(_notifier.Pushed);
    }

    [Fact]
    public async Task A_failed_push_is_retried_on_the_next_pass()
    {
        _subscriptions.Subscribe("c1", "AAPL", 10);
        await SeedPriceAsync("AAPL", 100m, T0);

        _notifier.FailNext = true;
        Assert.Equal(0, await _broadcaster.RunOnceAsync());

        Assert.Equal(1, await _broadcaster.RunOnceAsync());
        Assert.Single(_notifier.Pushed);
    }

    [Fact]
    public async Task The_loop_survives_a_failing_pass_and_keeps_pushing()
    {
        // Start the real loop with a broken database first, then a working one.
        var brokenServices = new ServiceCollection();
        brokenServices.AddDbContext<AppDbContext>(options => options.UseSqlServer(
            ConnectionString.Replace("WaxyCandlesTests_", "Missing_"),
            sql => sql.EnableRetryOnFailure(0)));
        await using var broken = brokenServices.BuildServiceProvider();

        var scopes = new SwitchingScopeFactory(broken.GetRequiredService<IServiceScopeFactory>());

        using var loop = new LivePriceBroadcaster(
            scopes,
            _subscriptions,
            _notifier,
            Microsoft.Extensions.Options.Options.Create(new LivePricesOptions { PollInterval = TimeSpan.FromMilliseconds(500) }),
            TimeProvider.System,
            NullLogger<LivePriceBroadcaster>.Instance);

        _subscriptions.Subscribe("c1", "AAPL", 10);
        await SeedPriceAsync("AAPL", 100m, T0);

        await loop.StartAsync(CancellationToken.None);

        await Eventually.TrueAsync(() => scopes.Created >= 2, TimeSpan.FromSeconds(30), "two failing passes");
        Assert.Empty(_notifier.Pushed);

        scopes.Target = _services.GetRequiredService<IServiceScopeFactory>();

        await Eventually.TrueAsync(() => _notifier.Pushed.Count == 1, TimeSpan.FromSeconds(30), "a push after recovery");

        await loop.StopAsync(CancellationToken.None);
    }

    private async Task SeedPriceAsync(
        string symbol,
        decimal price,
        DateTimeOffset at,
        decimal change = 0m,
        decimal changePercent = 0m)
    {
        await using var db = NewDbContext();
        db.StockPrices.Add(new StockPrice
        {
            Id = Guid.NewGuid(),
            Symbol = symbol,
            Price = price,
            Change = change,
            ChangePercent = changePercent,
            Timestamp = at
        });
        await db.SaveChangesAsync();
    }

    private sealed class RecordingLivePriceNotifier : ILivePriceNotifier
    {
        private readonly ConcurrentQueue<LivePriceUpdate> _pushed = new();

        public volatile bool FailNext;

        public IReadOnlyList<LivePriceUpdate> Pushed => _pushed.ToList();

        public Task PriceUpdatedAsync(LivePriceUpdate update, CancellationToken cancellationToken = default)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("Simulated push failure.");
            }

            _pushed.Enqueue(update);
            return Task.CompletedTask;
        }
    }

    /// <summary>Counts reader commands (queries) sent to SQL Server.</summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class SwitchingScopeFactory(IServiceScopeFactory target) : IServiceScopeFactory
    {
        private int _created;

        public volatile IServiceScopeFactory Target = target;

        public int Created => Volatile.Read(ref _created);

        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref _created);
            return Target.CreateScope();
        }
    }
}
