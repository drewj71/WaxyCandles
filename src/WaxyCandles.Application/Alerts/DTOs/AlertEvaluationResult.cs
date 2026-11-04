using WaxyCandles.Domain.Enums;

namespace WaxyCandles.Application.Alerts.DTOs;

/// <summary>An alert that one evaluation fired. <see cref="Level"/> is null for a plain price alert.</summary>
public sealed record FiredAlert(Guid AlertId, GexLevelKind? Level);

/// <summary>What one evaluation of a price did.</summary>
public sealed record AlertEvaluationResult(IReadOnlyList<FiredAlert> Fired)
{
    public static readonly AlertEvaluationResult None = new([]);
}
