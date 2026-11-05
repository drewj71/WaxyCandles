namespace WaxyCandles.Application.Backtesting;

/// <summary>
/// One strategy's results. Percentages are in percent (12.5 = 12.5%) rounded
/// to 2 decimals; money to 2 decimals.
/// </summary>
/// <param name="FinalEquity">Cash plus any open position at the last close.</param>
/// <param name="CagrPercent">Compound annual growth over the calendar time from the first to the last bar (365.25-day years); null for a single day, or when annualizing a very short test gives more than 1,000,000%.</param>
/// <param name="MaxDrawdownPercent">Largest fall from a closing-equity peak, as a positive percentage (0 if equity never fell).</param>
/// <param name="MaxDrawdownPeakDate">The peak before the largest fall; null if none.</param>
/// <param name="MaxDrawdownTroughDate">The bottom of the largest fall; null if none.</param>
/// <param name="SharpeRatio">
/// Mean over standard deviation (sample) of daily close-to-close equity
/// returns over every bar (flat days count as 0), times √252, risk-free rate 0.
/// Null with fewer than two returns or no variation.
/// </param>
/// <param name="TradeCount">Closed trades; an open position at the end isn't counted.</param>
/// <param name="WinRatePercent">Closed trades with a positive P&amp;L after costs; null without closed trades.</param>
/// <param name="AverageTradeReturnPercent">Mean return of closed trades; null without closed trades.</param>
/// <param name="ExposurePercent">Bars that closed with a position held.</param>
public sealed record BacktestMetrics(
    decimal FinalEquity,
    decimal TotalReturnPercent,
    decimal? CagrPercent,
    decimal MaxDrawdownPercent,
    DateOnly? MaxDrawdownPeakDate,
    DateOnly? MaxDrawdownTroughDate,
    decimal? SharpeRatio,
    int TradeCount,
    decimal? WinRatePercent,
    decimal? AverageTradeReturnPercent,
    decimal ExposurePercent,
    bool HasOpenPosition);

/// <summary>
/// A round trip. Prices are fills (slippage included), rounded to 4 decimals;
/// shares to 6. P&amp;L is after both commissions, against the cash spent on
/// entry (fill value plus commission). An open trade is marked at the last
/// close with no exit costs, and its exit date is the last bar's.
/// </summary>
public sealed record BacktestTrade(
    DateOnly EntryDate,
    decimal EntryPrice,
    DateOnly ExitDate,
    decimal ExitPrice,
    decimal Shares,
    decimal ProfitLoss,
    decimal ReturnPercent,
    bool IsOpen);

/// <summary>Equity at a bar's close, rounded to 2 decimals.</summary>
public sealed record BacktestEquityPoint(
    DateOnly Date,
    decimal Strategy,
    decimal Benchmark);

/// <param name="Equity">Unrounded equity at each bar's close.</param>
public sealed record BacktestRun(
    BacktestMetrics Metrics,
    IReadOnlyList<BacktestTrade> Trades,
    IReadOnlyList<decimal> Equity);

/// <summary>The chosen strategy and the buy-and-hold benchmark over the same bars and costs.</summary>
public sealed record BacktestResult(
    BacktestRun Strategy,
    BacktestRun Benchmark,
    IReadOnlyList<BacktestEquityPoint> EquityCurve);
