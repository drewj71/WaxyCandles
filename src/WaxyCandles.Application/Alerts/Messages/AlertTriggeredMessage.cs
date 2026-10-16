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

    /// <summary>
    /// The GEX level a level alert was approaching, or null for a price alert.
    /// Added later: messages written before it deserialize with null, which
    /// reads as a price alert.
    /// </summary>
    public GexLevelKind? Level { get; set; }

    /// <summary>The level's strike, set with <see cref="Level"/>.</summary>
    public decimal? LevelPrice { get; set; }
}
