using WaxyCandles.Domain.Enums;

namespace WaxyCandles.Domain.Entities;

public class Notification
{
    public Guid Id { get; set; }

    public string UserId { get; set; } = default!;

    public NotificationType Type { get; set; }

    public string Title { get; set; } = default!;

    public string Body { get; set; } = default!;

    public Guid? AlertId { get; set; }

    /// <summary>
    /// The message this notification was created from. Unique, which makes
    /// creating a notification idempotent under at-least-once delivery.
    /// </summary>
    public Guid SourceMessageId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ReadAt { get; set; }
}
