using WaxyCandles.Domain.Enums;

namespace WaxyCandles.Application.Alerts.Messages;

/// <summary>
/// Raised (through the outbox) when a price update triggers an alert.
/// </summary>
public class AlertTriggeredMessage
{
    public Guid MessageId { get; set; }

    public Guid AlertId { get; set; }

    public string UserId { get; set; } = default!;

    public string Symbol { get; set; } = default!;

    public AlertDirection Direction { get; set; }

    public decimal TargetPrice { get; set; }

    public decimal TriggerPrice { get; set; }

    public DateTimeOffset TriggeredAt { get; set; }
}
