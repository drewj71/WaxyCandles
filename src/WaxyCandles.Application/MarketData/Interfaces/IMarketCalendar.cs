namespace WaxyCandles.Application.MarketData.Interfaces;

/// <summary>Which dates US equity markets trade on (weekends, holidays).</summary>
public interface IMarketCalendar
{
    /// <summary>
    /// Whether the market trades on this US/Eastern date. When the answer isn't
    /// known, implementations say true for weekdays: polling on a holiday only
    /// wastes credits, while skipping a trading day would silence every alert.
    /// </summary>
    Task<bool> IsTradingDayAsync(
        DateOnly date,
        CancellationToken cancellationToken = default);
}
