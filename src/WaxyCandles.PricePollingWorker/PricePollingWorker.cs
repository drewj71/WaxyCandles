using WaxyCandles.Application.MarketData.Interfaces;

namespace WaxyCandles.PricePollingWorker;

public class PricePollingWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PricePollingWorker> _logger;

    public PricePollingWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<PricePollingWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
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
                TimeSpan.FromMinutes(5),
                stoppingToken);
        }
    }
}