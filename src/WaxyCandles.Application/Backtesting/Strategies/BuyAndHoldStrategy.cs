namespace WaxyCandles.Application.Backtesting.Strategies;

/// <summary>
/// The benchmark: buy on the first bar's signal, so under the same rule as
/// every strategy it fills at the second bar's open, then never sell.
/// </summary>
public sealed class BuyAndHoldStrategy : IBacktestStrategy
{
    public static readonly BuyAndHoldStrategy Instance = new();

    public int WarmupBars => 0;

    public IBarSignalSource Start() => new Source();

    private sealed class Source : IBarSignalSource
    {
        public TradeSignal OnBarClose(BacktestBar bar) => TradeSignal.Buy;
    }
}
