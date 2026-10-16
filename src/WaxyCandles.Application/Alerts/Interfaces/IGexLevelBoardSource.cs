using WaxyCandles.Application.Alerts.Levels;

namespace WaxyCandles.Application.Alerts.Interfaces;

public interface IGexLevelBoardSource
{
    /// <summary>
    /// The symbol's GEX levels for a board window, or null when there's no
    /// quote or option data. Throws when the provider fails.
    /// </summary>
    Task<GexLevelBoard?> GetAsync(
        string symbol,
        int days,
        decimal strikeRangePercent,
        CancellationToken cancellationToken = default);
}
