using WaxyCandles.Application.Backtesting.Indicators;

namespace WaxyCandles.Application.Backtesting.Strategies;

/// <summary>
/// Buy while RSI closes below <c>oversold</c>, sell while it closes above
/// <c>overbought</c>. RSI first exists at bar index <c>period</c>.
/// </summary>
public sealed class RsiMeanReversionStrategy(
    int period,
    decimal oversold,
    decimal overbought) : IBacktestStrategy
{
    public int WarmupBars => period;

    public IBarSignalSource Start() => new Source(new RelativeStrengthIndex(period), oversold, overbought);

    private sealed class Source(
        RelativeStrengthIndex rsi,
        decimal oversold,
        decimal overbought) : IBarSignalSource
    {
        public TradeSignal OnBarClose(BacktestBar bar) => rsi.Add(bar.Close) switch
        {
            decimal value when value < oversold => TradeSignal.Buy,
            decimal value when value > overbought => TradeSignal.Sell,
            _ => TradeSignal.None
        };
    }
}
