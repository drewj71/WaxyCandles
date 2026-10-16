using WaxyCandles.Domain.Enums;

namespace WaxyCandles.Application.Alerts.Levels;

/// <summary>
/// Turns "approaching a GEX level" into a plain price condition, so a level
/// alert is evaluated exactly like a price alert.
/// </summary>
public static class GexLevelTarget
{
    /// <summary>
    /// Below the level, the alert fires on rising to within the proximity of
    /// it; at or above it, on falling to within the proximity. A price already
    /// within range meets the condition straight away.
    /// </summary>
    /// <param name="proximityPercent">0.5 means within 0.5% of the level.</param>
    public static (decimal TargetPrice, AlertDirection Direction) For(
        decimal levelPrice,
        decimal currentPrice,
        decimal proximityPercent)
    {
        var offset = levelPrice * proximityPercent / 100m;

        // Stored as decimal(18,6).
        return currentPrice < levelPrice
            ? (Math.Round(levelPrice - offset, 6), AlertDirection.Above)
            : (Math.Round(levelPrice + offset, 6), AlertDirection.Below);
    }

    public static Options.Gex.GexNode? Pick(Options.Gex.GexLevels levels, GexLevelKind kind) => kind switch
    {
        GexLevelKind.King => levels.King,
        GexLevelKind.Gatekeeper => levels.Gatekeeper,
        GexLevelKind.Floor => levels.Floor,
        GexLevelKind.Ceiling => levels.Ceiling,
        _ => null
    };
}
