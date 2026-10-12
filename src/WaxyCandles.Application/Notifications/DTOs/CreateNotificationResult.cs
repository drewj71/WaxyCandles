namespace WaxyCandles.Application.Notifications.DTOs;

public enum CreateNotificationStatus
{
    Created,

    /// <summary>Already created from this message (a redelivery).</summary>
    Duplicate,

    /// <summary>The recipient no longer exists, so there is no one to notify.</summary>
    RecipientNotFound
}

public sealed record CreateNotificationResult(
    CreateNotificationStatus Status,
    NotificationResponse? Notification = null);
