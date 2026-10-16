using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;

namespace WaxyCandles.Application.Alerts.DTOs;

public sealed class AlertResponse
{
    public Guid Id { get; init; }

    public string Symbol { get; init; } = string.Empty;

    public decimal TargetPrice { get; init; }

    public AlertDirection Direction { get; init; }

    public AlertFrequency Frequency { get; init; }

    public int CooldownMinutes { get; init; }

    public bool IsEnabled { get; init; }

    public bool IsTriggered { get; init; }

    public AlertStatus Status { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>When it last fired.</summary>
    public DateTimeOffset? TriggeredAt { get; init; }

    public int TriggerCount { get; init; }

    public GexLevelKind? Level { get; init; }

    public decimal? LevelProximityPercent { get; init; }

    public int? LevelDays { get; init; }

    public decimal? LevelStrikeRange { get; init; }

    /// <summary>The level's strike when the target was last resolved.</summary>
    public decimal? LevelPrice { get; init; }

    /// <summary>The option chain snapshot the level came from.</summary>
    public DateTimeOffset? LevelAsOf { get; init; }

    public static AlertResponse From(Alert alert, DateTimeOffset now) => new()
    {
        Id = alert.Id,
        Symbol = alert.Symbol,
        TargetPrice = alert.TargetPrice,
        Direction = alert.Direction,
        Frequency = alert.Frequency,
        CooldownMinutes = alert.CooldownMinutes,
        IsEnabled = alert.IsEnabled,
        IsTriggered = alert.IsTriggered,
        Status = alert.GetStatus(now),
        CreatedAt = alert.CreatedAt,
        ExpiresAt = alert.ExpiresAt,
        TriggeredAt = alert.TriggeredAt,
        TriggerCount = alert.TriggerCount,
        Level = alert.Level,
        LevelProximityPercent = alert.LevelProximityPercent,
        LevelDays = alert.LevelDays,
        LevelStrikeRange = alert.LevelStrikeRange,
        LevelPrice = alert.LevelPrice,
        LevelAsOf = alert.LevelAsOf
    };
}
