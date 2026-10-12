using WaxyCandles.Domain.Enums;

namespace WaxyCandles.Application.Notifications.DTOs;

public sealed class NotificationResponse
{
    public Guid Id { get; init; }

    public NotificationType Type { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Body { get; init; } = string.Empty;

    public Guid? AlertId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ReadAt { get; init; }
}
