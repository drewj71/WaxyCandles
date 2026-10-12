using Microsoft.AspNetCore.SignalR;
using WaxyCandles.Application.Notifications.DTOs;
using WaxyCandles.Application.Notifications.Interfaces;

namespace WaxyCandles.Api.Hubs;

/// <summary>
/// Reaches only clients connected to this API instance. Running more than one
/// instance needs a SignalR backplane (e.g. Redis) for every client to get it.
/// </summary>
public class SignalRNotifier : IRealtimeNotifier
{
    private readonly IHubContext<NotificationsHub> _hub;

    public SignalRNotifier(IHubContext<NotificationsHub> hub)
    {
        _hub = hub;
    }

    public Task NotifyAsync(
        string userId,
        NotificationResponse notification,
        CancellationToken cancellationToken = default) =>
        _hub.Clients
            .User(userId)
            .SendAsync(
                NotificationsHub.NotificationReceived,
                notification,
                cancellationToken);
}
