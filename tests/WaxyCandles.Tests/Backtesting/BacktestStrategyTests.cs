using WaxyCandles.Application.Backtesting;
using WaxyCandles.Application.Backtesting.Indicators;
using WaxyCandles.Application.Backtesting.Strategies;
using static WaxyCandles.Application.Backtesting.TradeSignal;
using static WaxyCandles.Tests.Backtesting.BacktestTestData;

namespace WaxyCandles.Tests.Backtesting;

/// <summary>Indicators and each strategy's signals on hand-computed series.</summary>
public class BacktestStrategyTests
{
    [Fact]
    public void Sma_has_a_value_once_the_window_is_full()
    {
        var sma = new SimpleMovingAverage(3);

        Assert.Equal(
            [null, null, 2m, 3m, 4m],
            new[] { 1m, 2m, 3m, 4m, 5m }.Select(x => sma.Add(x)));
    }

    [Fact]
    public void Ema_is_seeded_with_the_sma_then_smoothed()
    {
        // Period 3: smoothing 2 / 4 = 0.5. Seed (1 + 2 + 3) / 3 = 2; then 2 + (4 − 2) × 0.5 = 3; 3 + (10 − 3) × 0.5 = 6.5.
        var ema = new ExponentialMovingAverage(3);

        Assert.Equal(
            [null, null, 2m, 3m, 6.5m],
            new[] { 1m, 2m, 3m, 4m, 10m }.Select(x => ema.Add(x)));
    }

    [Fact]
    public void Rsi_uses_wilder_smoothing()
    {
        var rsi = new RelativeStrengthIndex(2);

        var values = new[] { 10m, 11m, 10m, 12m, 8m }.Select(x => rsi.Add(x)).ToList();

        Assert.Null(values[0]);
        Assert.Null(values[1]);
        // Changes +1, −1: average gain 0.5, loss 0.5.
        Assert.Equal(50m, values[2]);
        // +2: gain (0.5 + 2) / 2 = 1.25, loss 0.5 / 2 = 0.25, RS 5.
        Assert.Equal(83.33m, Math.Round(values[3]!.Value, 2));
        // −4: gain 0.625, loss 2.125.
        Assert.Equal(22.73m, Math.Round(values[4]!.Value, 2));
    }

    [Fact]
    public void Rsi_is_100_with_no_losses_and_50_with_no_movement()
    {
        var rising = new RelativeStrengthIndex(2);
        var flat = new RelativeStrengthIndex(2);

        Assert.Equal(100m, new[] { 1m, 2m, 3m }.Select(x => rising.Add(x)).ToList()[^1]);
        Assert.Equal(50m, new[] { 5m, 5m, 5m }.Select(x => flat.Add(x)).ToList()[^1]);
    }

    [Fact]
    public void Rolling_extreme_excludes_the_value_being_added()
    {
        var high = new RollingExtreme(2, highest: true);

        Assert.Equal(
            [null, null, 11m, 12m, 12m],
            new[] { 10m, 11m, 12m, 9m, 8m }.Select(x => high.Add(x)));
    }

    [Fact]
    public void Sma_crossover_buys_on_a_cross_above_and_sells_on_a_cross_below()
    {
        var strategy = new MovingAverageCrossoverStrategy(2, 3, MovingAverageType.Sma);

        // Fast/slow: bar 2 10/10, bar 3 11.5/11 (cross above), bar 4 10/10 (touching
        // isn't crossing), bar 5 7/9 (cross below), bar 6 9.5/8.67 (cross above).
        Assert.Equal(
            [None, None, None, Buy, None, Sell, Buy],
            Signals(strategy, Closes(10m, 10m, 10m, 13m, 7m, 7m, 12m)));
        Assert.Equal(3, strategy.WarmupBars);
    }

    [Fact]
    public void Ema_crossover_uses_the_exponential_averages()
    {
        var strategy = new MovingAverageCrossoverStrategy(2, 3, MovingAverageType.Ema);

        // Fast (k = 2/3) / slow (k = 1/2): bar 2 10/10, bar 3 12/11.5 (above),
        // bar 4 8.67/9.25 (below), bar 5 7.56/8.13, bar 6 10.52/10.06 (above).
        // The SMA pair only touches at bar 4, so the EMA sells a bar earlier.
        Assert.Equal(
            [None, None, None, Buy, Sell, None, Buy],
            Signals(strategy, Closes(10m, 10m, 10m, 13m, 7m, 7m, 12m)));
    }

    [Fact]
    public void Ma_crossover_needs_an_actual_cross_not_fast_already_above()
    {
        // Rising throughout: fast is above slow from the first bar both exist, but never crosses.
        var signals = Signals(new MovingAverageCrossoverStrategy(2, 3, MovingAverageType.Sma), Closes(1m, 2m, 3m, 4m, 5m, 6m));

        Assert.All(signals, x => Assert.Equal(None, x));
    }

    [Fact]
    public void Rsi_strategy_buys_below_oversold_and_sells_above_overbought()
    {
        var strategy = new RsiMeanReversionStrategy(2, 30m, 70m);

        // RSI: –, –, 50, 83.3, 22.7 (see Rsi_uses_wilder_smoothing).
        Assert.Equal(
            [None, None, None, Sell, Buy],
            Signals(strategy, Closes(10m, 11m, 10m, 12m, 8m)));
        Assert.Equal(2, strategy.WarmupBars);
    }

    [Fact]
    public void Breakout_compares_the_close_with_the_previous_bars_channel()
    {
        var strategy = new DonchianBreakoutStrategy(2, 2);
        var bars = new List<BacktestBar>
        {
            new(Start, 9m, 10m, 8m, 9m),
            new(Start.AddDays(1), 10m, 11m, 9m, 10m),
            // Above max(10, 11); its own high of 12 isn't part of the channel.
            new(Start.AddDays(2), 11m, 12m, 10m, 11.5m),
            new(Start.AddDays(3), 11m, 12m, 10m, 11m),
            // Below min(10, 10).
            new(Start.AddDays(4), 10m, 11m, 8.5m, 8.9m)
        };

        Assert.Equal([None, None, Buy, None, Sell], Signals(strategy, bars));
    }

    [Fact]
    public void Breakout_warm_up_is_the_longer_channel()
    {
        Assert.Equal(20, new DonchianBreakoutStrategy(20, 10).WarmupBars);
        Assert.Equal(55, new DonchianBreakoutStrategy(20, 55).WarmupBars);
    }

    [Fact]
    public void A_crossover_trades_through_the_engine_at_the_next_open()
    {
        // Buy signal at bar 3 (close 13), sell signal at bar 5 (close 7).
        var bars = OpenClose((10m, 10m), (10m, 10m), (10m, 10m), (12m, 13m), (12.5m, 7m), (7m, 7m), (6m, 6m));

        var run = BacktestEngine.Run(bars, new MovingAverageCrossoverStrategy(2, 3, MovingAverageType.Sma), new BacktestCosts());

        var trade = Assert.Single(run.Trades);
        Assert.Equal(Start.AddDays(4), trade.EntryDate);
        Assert.Equal(12.5m, trade.EntryPrice);
        Assert.Equal(Start.AddDays(6), trade.ExitDate);
        Assert.Equal(6m, trade.ExitPrice);
        Assert.Equal(-52m, trade.ReturnPercent);
    }
}
