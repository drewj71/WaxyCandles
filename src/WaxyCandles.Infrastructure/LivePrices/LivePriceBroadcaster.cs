using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WaxyCandles.Application.MarketData.LivePrices;
using WaxyCandles.Infrastructure.MarketData;
using WaxyCandles.Infrastructure.Persistence;
using WaxyCandles.Infrastructure.Telemetry;

namespace WaxyCandles.Infrastructure.LivePrices;

/// <summary>
/// Pushes newly stored prices to the browsers on this API instance. Every
/// <see cref="LivePricesOptions.PollInterval"/> it reads the latest stored
/// price of each symbol some connection watches (one query, none when nobody
/// watches) and pushes the ones newer than what it last pushed.
/// <para>
/// It only reads <c>StockPrices</c>: no RabbitMQ, outbox or vendor call. A
/// failed pass (e.g. the database is down, after EF's own retries) is logged
/// and the next pass tries again; it never stops the host.
/// </para>
/// </summary>
public sealed class LivePriceBroadcaster : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly LivePriceSubscriptions _subscriptions;
    private readonly ILivePriceNotifier _notifier;
    private readonly LivePricesOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LivePriceBroadcaster> _logger;

    // The timestamp last pushed per symbol. Only the loop touches it. A symbol
    // nobody watches any more is forgotten, so when it is watched again its
    // current price is pushed once (the new watcher's starting value).
    private readonly Dictionary<string, DateTimeOffset> _lastPushed = new(StringComparer.OrdinalIgnoreCase);

    private int _consecutiveFailures;

    public LivePriceBroadcaster(
        IServiceScopeFactory scopeFactory,
        LivePriceSubscriptions subscriptions,
        ILivePriceNotifier notifier,
        IOptions<LivePricesOptions> options,
        TimeProvider timeProvider,
        ILogger<LivePriceBroadcaster> logger)
    {
        _scopeFactory = scopeFactory;
        _subscriptions = subscriptions;
        _notifier = notifier;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
                OnPassSucceeded();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                OnPassFailed(ex);
            }

            try
            {
                await Task.Delay(_options.PollInterval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// One pass. Returns how many prices were pushed. Not thread-safe: only
    /// the loop (or a test, instead of the loop) calls it.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var symbols = _subscriptions.Symbols();

        foreach (var forgotten in _lastPushed.Keys.Except(symbols, StringComparer.OrdinalIgnoreCase).ToList())
        {
            _lastPushed.Remove(forgotten);
        }

        if (symbols.Count == 0)
        {
            return 0;
        }

        List<Domain.Entities.StockPrice> latest;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            latest = await LatestStockPrices.For(dbContext, symbols)
                .ToListAsync(cancellationToken);

            WaxyCandlesMetrics.LivePriceChecks.Add(1, Outcome("success"));
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            WaxyCandlesMetrics.LivePriceChecks.Add(1, Outcome("failure"));
            throw;
        }

        var pushed = 0;

        foreach (var price in latest)
        {
            if (_lastPushed.TryGetValue(price.Symbol, out var last) && price.Timestamp <= last)
            {
                continue;
            }

            var update = new LivePriceUpdate(
                price.Symbol,
                price.Price,
                price.Change,
                price.ChangePercent,
                price.Price - price.Change,
                price.Timestamp);

            try
            {
                await _notifier.PriceUpdatedAsync(update, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Left unrecorded, so the next pass pushes it again.
                _logger.LogWarning(ex, "Pushing a live price for {Symbol} failed.", price.Symbol);
                continue;
            }

            _lastPushed[price.Symbol] = price.Timestamp;
            pushed++;
        }

        WaxyCandlesMetrics.LivePricesPushed.Add(pushed);

        return pushed;
    }

    private void OnPassSucceeded()
    {
        if (_consecutiveFailures > 0)
        {
            _logger.LogInformation(
                "Live prices recovered after {Failures} failed passes.",
                _consecutiveFailures);
        }

        _consecutiveFailures = 0;
    }

    // One warning per outage, not one every couple of seconds.
    private void OnPassFailed(Exception ex)
    {
        if (_consecutiveFailures++ == 0)
        {
            _logger.LogWarning(ex, "Reading live prices failed; retrying every {Interval}.", _options.PollInterval);
        }
        else
        {
            _logger.LogDebug(ex, "Reading live prices failed again ({Failures} in a row).", _consecutiveFailures);
        }
    }

    private static KeyValuePair<string, object?> Outcome(string outcome) =>
        new(WaxyCandlesMetrics.OutcomeTag, outcome);
}
