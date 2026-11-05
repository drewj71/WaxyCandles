using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using WaxyCandles.Application.MarketData;
using WaxyCandles.Infrastructure.LivePrices;

namespace WaxyCandles.Api.Hubs;

/// <summary>
/// Live prices. A client calls <see cref="Subscribe"/> for each symbol it
/// shows and gets <see cref="PriceUpdated"/> whenever a newer price is stored
/// (see <see cref="LivePriceBroadcaster"/>). Separate from
/// <see cref="NotificationsHub"/>: that one is server-to-client per user, this
/// one keeps per-connection state, and the browser opens it only on pages that
/// show prices. Authenticated by the same access-token cookie as the REST API.
/// Subscriptions live in this instance's memory and end with the connection,
/// so a reconnecting client subscribes again.
/// </summary>
[Authorize]
public class PricesHub : Hub
{
    public const string Path = "/hubs/prices";

    /// <summary>Client method name for a new price.</summary>
    public const string PriceUpdated = "priceUpdated";

    private readonly LivePriceSubscriptions _subscriptions;
    private readonly LivePricesOptions _options;

    public PricesHub(
        LivePriceSubscriptions subscriptions,
        IOptions<LivePricesOptions> options)
    {
        _subscriptions = subscriptions;
        _options = options.Value;
    }

    public static string GroupFor(string symbol) => $"prices:{symbol}";

    /// <summary>Errors reach the caller as a <see cref="HubException"/> message.</summary>
    public async Task Subscribe(string symbol)
    {
        if (!StockSymbol.TryNormalize(symbol, out var normalized))
        {
            throw new HubException(StockSymbol.InvalidMessage);
        }

        var result = _subscriptions.Subscribe(
            Context.ConnectionId,
            normalized,
            _options.MaxSubscriptionsPerConnection);

        if (result == SubscribeResult.LimitReached)
        {
            throw new HubException(
                $"A connection can watch at most {_options.MaxSubscriptionsPerConnection} symbols.");
        }

        if (result == SubscribeResult.Added)
        {
            try
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(normalized));
            }
            catch
            {
                _subscriptions.Unsubscribe(Context.ConnectionId, normalized);
                throw;
            }
        }
    }

    public async Task Unsubscribe(string symbol)
    {
        var normalized = StockSymbol.Normalize(symbol);

        if (_subscriptions.Unsubscribe(Context.ConnectionId, normalized))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(normalized));
        }
    }

    // SignalR drops the connection's groups itself.
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _subscriptions.RemoveConnection(Context.ConnectionId);

        return base.OnDisconnectedAsync(exception);
    }
}
