using WaxyCandles.Application.Backtesting.Indicators;

namespace WaxyCandles.Application.Backtesting.Strategies;

/// <summary>
/// Donchian channel breakout: buy when a bar closes above the highest high of
/// the previous <c>entryPeriod</c> bars, sell when one closes below the lowest
/// low of the previous <c>exitPeriod</c> bars. Today's bar is never part of its
/// own channel. No signal until both channels are formed (bar index
/// max(entry, exit)).
/// </summary>
public sealed class DonchianBreakoutStrategy(
    int entryPeriod,
    int exitPeriod) : IBacktestStrategy
{
    public int WarmupBars => Math.Max(entryPeriod, exitPeriod);

    public IBarSignalSource Start() => new Source(
        new RollingExtreme(entryPeriod, highest: true),
        new RollingExtreme(exitPeriod, highest: false));

    private sealed class Source(RollingExtreme highs, RollingExtreme lows) : IBarSignalSource
    {
        public TradeSignal OnBarClose(BacktestBar bar)
        {
            var upper = highs.Add(bar.High);
            var lower = lows.Add(bar.Low);

            if (upper is null || lower is null)
            {
                return TradeSignal.None;
            }

            if (bar.Close > upper)
            {
                return TradeSignal.Buy;
            }

            return bar.Close < lower ? TradeSignal.Sell : TradeSignal.None;
        }
    }
}
