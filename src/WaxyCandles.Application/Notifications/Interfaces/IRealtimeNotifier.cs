using WaxyCandles.Application.Notifications.DTOs;

namespace WaxyCandles.Application.Notifications.Interfaces;

/// <summary>
/// Pushes a notification to the user's connected clients. Best effort: the
/// notification is already stored, so a client that misses the push still
/// sees it on its next fetch.
/// </summary>
public interface IRealtimeNotifier
{
    Task NotifyAsync(
        string userId,
        NotificationResponse notification,
        CancellationToken cancellationToken = default);
}
