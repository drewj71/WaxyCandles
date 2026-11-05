using WaxyCandles.Application.Backtesting.Indicators;

namespace WaxyCandles.Application.Backtesting.Strategies;

public enum MovingAverageType
{
    Sma,
    Ema
}

/// <summary>
/// Buy when the fast average of closes crosses above the slow one (it was at
/// or below it on the previous bar), sell when it crosses below. Both averages
/// must exist on the previous bar, so the first signal can come at bar index
/// <c>slowPeriod</c>.
/// </summary>
public sealed class MovingAverageCrossoverStrategy(
    int fastPeriod,
    int slowPeriod,
    MovingAverageType type) : IBacktestStrategy
{
    public int WarmupBars => slowPeriod;

    public IBarSignalSource Start() => new Source(Create(fastPeriod), Create(slowPeriod));

    private IMovingAverage Create(int period) => type == MovingAverageType.Ema
        ? new ExponentialMovingAverage(period)
        : new SimpleMovingAverage(period);

    private sealed class Source(IMovingAverage fast, IMovingAverage slow) : IBarSignalSource
    {
        private decimal? _previousFast;
        private decimal? _previousSlow;

        public TradeSignal OnBarClose(BacktestBar bar)
        {
            var currentFast = fast.Add(bar.Close);
            var currentSlow = slow.Add(bar.Close);

            var signal = TradeSignal.None;

            if (_previousFast is decimal pf && _previousSlow is decimal ps &&
                currentFast is decimal f && currentSlow is decimal s)
            {
                if (pf <= ps && f > s)
                {
                    signal = TradeSignal.Buy;
                }
                else if (pf >= ps && f < s)
                {
                    signal = TradeSignal.Sell;
                }
            }

            _previousFast = currentFast;
            _previousSlow = currentSlow;

            return signal;
        }
    }
}
