using Microsoft.Extensions.Options;
using WaxyCandles.Application.Alerts.Interfaces;
using WaxyCandles.Application.MarketData;
using WaxyCandles.Application.MarketData.Interfaces;
using WaxyCandles.Infrastructure.Telemetry;

namespace WaxyCandles.PricePollingWorker;

public class PricePollingWorker : BackgroundService
{
    // After a failed GEX level refresh, wait this long before trying again,
    // so a provider outage doesn't cost a board request on every poll.
    private static readonly TimeSpan LevelRefreshRetryDelay = TimeSpan.FromMinutes(15);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PricePollingWorker> _logger;
    private readonly PricePollingOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IMarketCalendar _marketCalendar;

    private readonly DailyRefreshSchedule _levelRefresh = new(LevelRefreshRetryDelay);

    public PricePollingWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<PricePollingWorker> logger,
        IOptions<PricePollingOptions> options,
        TimeProvider timeProvider,
        IMarketCalendar marketCalendar)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
        _timeProvider = timeProvider;
        _marketCalendar = marketCalendar;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = _timeProvider.GetUtcNow();

            if (_options.MarketHoursOnly &&
                !await MarketHours.IsOpenAsync(now, _marketCalendar, stoppingToken))
            {
                var nextOpen = await MarketHours.NextOpenAsync(
                    now,
                    _marketCalendar,
                    stoppingToken);

                _logger.LogInformation(
                    "Market closed. Next price poll at {NextOpen:u}.",
                    nextOpen);

                await Task.Delay(
                    nextOpen - now,
                    _timeProvider,
                    stoppingToken);

                continue;
            }

            try
            {
                // Before the day's first prices, so they're checked against
                // today's levels.
                await RefreshGexLevelsIfDueAsync(now, stoppingToken);

                // The root of each price's trace.
                using var activity = WaxyCandlesTelemetry.Source.StartActivity("poll prices");

                using var scope = _scopeFactory.CreateScope();

                var marketDataService =
                    scope.ServiceProvider
                        .GetRequiredService<IMarketDataService>();

                await marketDataService
                    .UpdatePricesAsync(stoppingToken);

                _logger.LogInformation(
                    "Market data update completed.");
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error while updating market data and evaluating alerts.");
            }

            await Task.Delay(
                _options.Interval,
                _timeProvider,
                stoppingToken);
        }
    }

    /// <summary>
    /// Once per trading day (on the first poll, or on start-up mid-session),
    /// moves GEX level alerts' targets to today's levels. A failure is retried
    /// after <see cref="LevelRefreshRetryDelay"/>, and never stops price polling.
    /// </summary>
    private async Task RefreshGexLevelsIfDueAsync(
        DateTimeOffset now,
        CancellationToken stoppingToken)
    {
        if (!_levelRefresh.IsDue(now))
        {
            return;
        }

        try
        {
            using var activity = WaxyCandlesTelemetry.Source.StartActivity("refresh GEX level alerts");
            using var scope = _scopeFactory.CreateScope();

            var refresher = scope.ServiceProvider
                .GetRequiredService<IGexLevelAlertRefresher>();

            if (await refresher.RefreshAsync(stoppingToken))
            {
                _levelRefresh.Succeeded(now);
                return;
            }
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Error while refreshing GEX level alerts.");
        }

        _logger.LogWarning(
            "GEX level alerts not fully refreshed. Retrying after {RetryAt:u}.",
            _levelRefresh.Failed(now));
    }
}
