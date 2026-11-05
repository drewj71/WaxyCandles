namespace WaxyCandles.Application.Backtesting;

public enum TradeSignal
{
    None,
    Buy,
    Sell
}

/// <summary>
/// A long-only trading rule. The engine feeds it bars one at a time, oldest
/// first, so a signal can only depend on the bar just closed and earlier ones;
/// the engine executes it at the next bar's open. New strategies (and later a
/// rule builder) implement this without changing the engine.
/// </summary>
public interface IBacktestStrategy
{
    /// <summary>Bars at the start that can't produce a signal while the indicators fill.</summary>
    int WarmupBars { get; }

    /// <summary>A fresh signal source (indicator state) for one run.</summary>
    IBarSignalSource Start();
}

public interface IBarSignalSource
{
    /// <summary>
    /// Called once per bar after it closes, including warm-up bars (whose
    /// signals the engine ignores). <see cref="TradeSignal.Buy"/> while long and
    /// <see cref="TradeSignal.Sell"/> while flat are ignored too, so a strategy
    /// may report a state ("RSI is oversold") rather than only an event.
    /// </summary>
    TradeSignal OnBarClose(BacktestBar bar);
}
