using WaxyCandles.Domain.Enums;

namespace WaxyCandles.Domain.Entities;

public class Alert
{
    public const int DefaultCooldownMinutes = 15;

    public const decimal DefaultLevelProximityPercent = 0.5m;

    // The GEX page's defaults, so an alert made there follows the same board.
    public const int DefaultLevelDays = 30;

    public const decimal DefaultLevelStrikeRange = 10m;

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

    /// <summary>
    /// When the alert's state last changed: the observation time of the price
    /// that fired or re-armed it, or the time an edit re-armed it. A price
    /// observed before this can't change the state again, so prices evaluated
    /// out of order can't re-arm or fire it twice for one crossing.
    /// </summary>
    public DateTimeOffset? StateChangedAt { get; set; }

    /// <summary>
    /// Set for a GEX level alert: the target follows this node of the symbol's
    /// GEX board, re-resolved each trading day. TargetPrice and Direction are
    /// then derived ("approaching" the level from the price's side) rather than
    /// chosen. Null for a plain price alert.
    /// </summary>
    public GexLevelKind? Level { get; set; }

    /// <summary>How close to the level, as a percentage of it, counts as reaching it (0 = touching).</summary>
    public decimal? LevelProximityPercent { get; set; }

    /// <summary>The board's expiration window, in calendar days.</summary>
    public int? LevelDays { get; set; }

    /// <summary>The board's strike range, as a percentage either side of the price.</summary>
    public decimal? LevelStrikeRange { get; set; }

    /// <summary>The level's strike when the target was last resolved.</summary>
    public decimal? LevelPrice { get; set; }

    /// <summary>The option chain snapshot time the level was resolved from.</summary>
    public DateTimeOffset? LevelAsOf { get; set; }

    /// <summary>
    /// The US/Eastern trading date the level was last checked: resolved on
    /// create or edit, or handled by the daily refresh (even if the board had
    /// no data and the target was kept). The refresh skips alerts already
    /// checked today, so a retry or a worker restart mid-session can't
    /// re-resolve them against the current price, which could flip the
    /// direction and fire the same approach twice.
    /// </summary>
    public DateOnly? LevelCheckedOn { get; set; }

    public bool IsLevelAlert => Level is not null;

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
