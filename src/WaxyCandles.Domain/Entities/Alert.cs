using WaxyCandles.Domain.Enums;

namespace WaxyCandles.Domain.Entities;

public class Alert
{
    public const int DefaultCooldownMinutes = 15;

    public Guid Id { get; set; }

    public string UserId { get; set; } = default!;

    public string Symbol { get; set; } = default!;

    public decimal TargetPrice { get; set; }

    public AlertDirection Direction { get; set; }

    public AlertFrequency Frequency { get; set; } = AlertFrequency.Once;

    /// <summary>
    /// For <see cref="AlertFrequency.EveryCross"/>: minimum time between two fires,
    /// so a price wobbling around the target doesn't fire on every poll.
    /// </summary>
    public int CooldownMinutes { get; set; } = DefaultCooldownMinutes;

    /// <summary>True from firing until a repeating alert re-arms (one-time alerts stay true).</summary>
    public bool IsTriggered { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public bool IsEnabled { get; set; } = true;

    /// <summary>After this, the alert is no longer evaluated. Null means never.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>When it last fired.</summary>
    public DateTimeOffset? TriggeredAt { get; set; }

    public int TriggerCount { get; set; }

    public bool IsConditionMet(decimal price) => Direction switch
    {
        AlertDirection.Above => price >= TargetPrice,
        AlertDirection.Below => price <= TargetPrice,
        _ => false
    };

    public bool IsExpired(DateTimeOffset now) => ExpiresAt <= now;

    public AlertStatus GetStatus(DateTimeOffset now) =>
        !IsEnabled ? AlertStatus.Disabled
        : IsExpired(now) ? AlertStatus.Expired
        : IsTriggered ? AlertStatus.Triggered
        : AlertStatus.Active;
}
