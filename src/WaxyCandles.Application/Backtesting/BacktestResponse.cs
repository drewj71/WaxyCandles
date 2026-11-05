namespace WaxyCandles.Application.Backtesting;

/// <summary>
/// A backtest's results: the strategy and the buy-and-hold benchmark over the
/// stored daily candles from <see cref="From"/> to <see cref="To"/> (which can
/// be narrower than requested if the provider has no older or newer data).
/// </summary>
/// <param name="WarmupBars">Bars at the start the strategy can't trade on while its indicators fill.</param>
/// <param name="DataImported">Whether missing history was imported from the provider for this request.</param>
/// <param name="Candles">The bars tested, for the chart.</param>
public sealed record BacktestResponse(
    string Symbol,
    BacktestStrategyType Strategy,
    DateOnly RequestedFrom,
    DateOnly RequestedTo,
    DateOnly From,
    DateOnly To,
    int BarCount,
    int WarmupBars,
    bool DataImported,
    BacktestCosts Costs,
    BacktestMetrics Metrics,
    BacktestMetrics Benchmark,
    IReadOnlyList<BacktestEquityPoint> EquityCurve,
    IReadOnlyList<BacktestTrade> Trades,
    IReadOnlyList<BacktestBar> Candles);
