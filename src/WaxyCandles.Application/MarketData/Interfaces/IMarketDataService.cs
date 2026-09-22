namespace WaxyCandles.Application.MarketData.Interfaces;

public interface IMarketDataService
{
    Task UpdatePricesAsync(
        CancellationToken cancellationToken = default);
}