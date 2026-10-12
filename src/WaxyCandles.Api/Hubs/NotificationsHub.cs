using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace WaxyCandles.Api.Hubs;

/// <summary>
/// Server-to-client only: pushes new notifications to the signed-in user's
/// connections (addressed by the JWT's NameIdentifier claim, SignalR's default
/// user id). Authenticated by the same access-token cookie as the REST API.
/// </summary>
[Authorize]
public class NotificationsHub : Hub
{
    public const string Path = "/hubs/notifications";

    /// <summary>Client method name for a new notification.</summary>
    public const string NotificationReceived = "notificationReceived";
}
