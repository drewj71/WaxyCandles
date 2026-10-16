using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;

namespace WaxyCandles.Application.Alerts.DTOs;

/// <summary>
/// Replaces the alert's settings (PUT semantics: omitted fields take their
/// defaults, so a missing ExpiresAt clears the expiry). The symbol can't change.
/// </summary>
public sealed class UpdateAlertRequest
{
    /// <summary>Ignored for a GEX level alert, whose target is derived from the level.</summary>
    public decimal TargetPrice { get; init; }

    /// <summary>Ignored for a GEX level alert, whose direction is derived from the level.</summary>
    public AlertDirection Direction { get; init; }

    /// <summary>Set to make this a GEX level alert; null for a price alert.</summary>
    public GexLevelKind? Level { get; init; }

    /// <summary>Level alerts: within this percentage of the level counts as reaching it.</summary>
    public decimal LevelProximityPercent { get; init; } = Alert.DefaultLevelProximityPercent;

    /// <summary>Level alerts: the board's expiration window in days.</summary>
    public int LevelDays { get; init; } = Alert.DefaultLevelDays;

    /// <summary>Level alerts: the board's strike range, percent either side of the price.</summary>
    public decimal LevelStrikeRange { get; init; } = Alert.DefaultLevelStrikeRange;

    public AlertFrequency Frequency { get; init; } = AlertFrequency.Once;

    public int CooldownMinutes { get; init; } = Alert.DefaultCooldownMinutes;

    public DateTimeOffset? ExpiresAt { get; init; }

    public bool IsEnabled { get; init; } = true;
}
