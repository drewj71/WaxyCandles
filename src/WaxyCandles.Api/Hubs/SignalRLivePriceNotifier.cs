using Microsoft.AspNetCore.SignalR;
using WaxyCandles.Application.MarketData.LivePrices;

namespace WaxyCandles.Api.Hubs;

/// <summary>
/// Sends to the symbol's group on this instance. No backplane is needed:
/// every instance runs its own broadcaster for its own connections.
/// </summary>
public class SignalRLivePriceNotifier : ILivePriceNotifier
{
    private readonly IHubContext<PricesHub> _hub;

    public SignalRLivePriceNotifier(IHubContext<PricesHub> hub)
    {
        _hub = hub;
    }

    public Task PriceUpdatedAsync(
        LivePriceUpdate update,
        CancellationToken cancellationToken = default) =>
        _hub.Clients
            .Group(PricesHub.GroupFor(update.Symbol))
            .SendAsync(PricesHub.PriceUpdated, update, cancellationToken);
}
