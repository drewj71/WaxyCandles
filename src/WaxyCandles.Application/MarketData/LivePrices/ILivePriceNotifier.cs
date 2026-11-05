namespace WaxyCandles.Application.MarketData.LivePrices;

/// <summary>Pushes a price to every client on this instance subscribed to its symbol.</summary>
public interface ILivePriceNotifier
{
    Task PriceUpdatedAsync(
        LivePriceUpdate update,
        CancellationToken cancellationToken = default);
}
