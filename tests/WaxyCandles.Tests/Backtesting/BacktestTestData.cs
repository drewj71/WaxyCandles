using WaxyCandles.Application.Backtesting;

namespace WaxyCandles.Tests.Backtesting;

internal static class BacktestTestData
{
    public static readonly DateOnly Start = new(2024, 1, 1);

    /// <summary>Consecutive days from <see cref="Start"/>; open, high and low all equal the close.</summary>
    public static List<BacktestBar> Closes(params decimal[] closes) =>
        closes.Select((close, i) => new BacktestBar(Start.AddDays(i), close, close, close, close)).ToList();

    /// <summary>Consecutive days from <see cref="Start"/> with their own opens and closes.</summary>
    public static List<BacktestBar> OpenClose(params (decimal Open, decimal Close)[] bars) =>
        bars.Select((bar, i) => new BacktestBar(
                Start.AddDays(i),
                bar.Open,
                Math.Max(bar.Open, bar.Close),
                Math.Min(bar.Open, bar.Close),
                bar.Close))
            .ToList();

    /// <summary>The signal the strategy gives after each bar, from a fresh source.</summary>
    public static List<TradeSignal> Signals(IBacktestStrategy strategy, IEnumerable<BacktestBar> bars)
    {
        var source = strategy.Start();
        return bars.Select(source.OnBarClose).ToList();
    }
}

/// <summary>
/// Gives the signal scripted for each bar index, and records how many bars it
/// had seen when it gave each one.
/// </summary>
internal sealed class ScriptedStrategy(
    IReadOnlyDictionary<int, TradeSignal> script,
    int warmupBars = 0) : IBacktestStrategy
{
    public List<DateOnly> Seen { get; } = [];

    private TradeSignal SignalAt(int index) => script.GetValueOrDefault(index, TradeSignal.None);

    public int WarmupBars => warmupBars;

    public IBarSignalSource Start() => new Source(this);

    private sealed class Source(ScriptedStrategy owner) : IBarSignalSource
    {
        public TradeSignal OnBarClose(BacktestBar bar)
        {
            var index = owner.Seen.Count;
            owner.Seen.Add(bar.Date);
            return owner.SignalAt(index);
        }
    }
}
