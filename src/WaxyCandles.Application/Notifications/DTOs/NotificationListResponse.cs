namespace WaxyCandles.Application.Notifications.DTOs;

public sealed class NotificationListResponse
{
    /// <summary>Most recent first.</summary>
    public IReadOnlyList<NotificationResponse> Items { get; init; } = [];

    /// <summary>Across all of the user's notifications, not just <see cref="Items"/>.</summary>
    public int UnreadCount { get; init; }
}
