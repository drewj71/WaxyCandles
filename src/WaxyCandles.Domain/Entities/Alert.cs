using WaxyCandles.Domain.Enums;

namespace WaxyCandles.Domain.Entities;

public class Alert
{
    public Guid Id { get; set; }

    public string UserId { get; set; } = default!;

    public string Symbol { get; set; } = default!;

    public decimal TargetPrice { get; set; }

    public AlertDirection Direction { get; set; }

    public bool IsTriggered { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset? TriggeredAt { get; set; }
}