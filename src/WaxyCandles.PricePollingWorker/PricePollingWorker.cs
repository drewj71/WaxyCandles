using Microsoft.Extensions.Options;
using WaxyCandles.Application.MarketData;
using WaxyCandles.Application.MarketData.Interfaces;

namespace WaxyCandles.PricePollingWorker;

public class PricePollingWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PricePollingWorker> _logger;
    private readonly PricePollingOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IMarketCalendar _marketCalendar;

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
}
