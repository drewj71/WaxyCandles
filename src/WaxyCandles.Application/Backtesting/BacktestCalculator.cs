using WaxyCandles.Application.Backtesting.Strategies;

namespace WaxyCandles.Application.Backtesting;

/// <summary>
/// Runs a strategy and the buy-and-hold benchmark over the same bars and costs.
/// Pure, like <see cref="BacktestEngine"/>.
/// </summary>
public static class BacktestCalculator
{
    public static BacktestResult Calculate(
        IReadOnlyList<BacktestBar> bars,
        IBacktestStrategy strategy,
        BacktestCosts costs)
    {
        var run = BacktestEngine.Run(bars, strategy, costs);
        var benchmark = BacktestEngine.Run(bars, BuyAndHoldStrategy.Instance, costs);

        var curve = bars
            .Select((bar, i) => new BacktestEquityPoint(
                bar.Date,
                Math.Round(run.Equity[i], 2, MidpointRounding.AwayFromZero),
                Math.Round(benchmark.Equity[i], 2, MidpointRounding.AwayFromZero)))
            .ToList();

        return new BacktestResult(run, benchmark, curve);
    }
}
