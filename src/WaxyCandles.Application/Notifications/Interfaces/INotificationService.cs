using WaxyCandles.Application.Alerts.Messages;
using WaxyCandles.Application.Notifications.DTOs;

namespace WaxyCandles.Application.Notifications.Interfaces;

public interface INotificationService
{
    /// <summary>
    /// Creates the notification for a triggered alert. Idempotent per message:
    /// a redelivery returns <see cref="CreateNotificationStatus.Duplicate"/>.
    /// </summary>
    Task<CreateNotificationResult> CreateForTriggeredAlertAsync(
        AlertTriggeredMessage message,
        CancellationToken cancellationToken = default);

    Task<NotificationListResponse> GetRecentAsync(
        string userId,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>Returns false if the notification doesn't exist or isn't the user's.</summary>
    Task<bool> MarkReadAsync(
        Guid id,
        string userId,
        CancellationToken cancellationToken = default);

    Task MarkAllReadAsync(
        string userId,
        CancellationToken cancellationToken = default);
}
