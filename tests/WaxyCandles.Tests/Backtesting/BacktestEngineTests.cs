using WaxyCandles.Application.Backtesting;
using WaxyCandles.Application.Backtesting.Strategies;
using static WaxyCandles.Tests.Backtesting.BacktestTestData;

namespace WaxyCandles.Tests.Backtesting;

/// <summary>
/// The engine on small hand-computed series: next-open execution, costs,
/// warm-up, an open position at the end, and the metrics.
/// </summary>
public class BacktestEngineTests
{
    private static readonly BacktestCosts NoCosts = new();

    private static ScriptedStrategy Script(params (int Bar, TradeSignal Signal)[] signals) =>
        new(signals.ToDictionary(x => x.Bar, x => x.Signal));

    [Fact]
    public void A_signal_on_a_close_fills_at_the_next_open()
    {
        var bars = OpenClose((10m, 10.5m), (11m, 11.5m), (12m, 12.5m), (13m, 13.5m));

        var run = BacktestEngine.Run(bars, Script((0, TradeSignal.Buy), (2, TradeSignal.Sell)), NoCosts);

        var trade = Assert.Single(run.Trades);
        Assert.Equal(Start.AddDays(1), trade.EntryDate);
        Assert.Equal(11m, trade.EntryPrice);
        Assert.Equal(Start.AddDays(3), trade.ExitDate);
        Assert.Equal(13m, trade.ExitPrice);
        // All in: 10,000 / 11 shares.
        Assert.Equal(909.090909m, trade.Shares);
        Assert.Equal(1818.18m, trade.ProfitLoss);
        Assert.Equal(18.18m, trade.ReturnPercent);
        Assert.False(trade.IsOpen);

        // Flat at bar 0's close, held at bars 1 and 2, cash again after bar 3's open.
        Assert.Equal(10_000m, run.Equity[0]);
        Assert.Equal(10_454.55m, Math.Round(run.Equity[1], 2));
        Assert.Equal(11_363.64m, Math.Round(run.Equity[2], 2));
        Assert.Equal(11_818.18m, Math.Round(run.Equity[3], 2));

        Assert.Equal(18.18m, run.Metrics.TotalReturnPercent);
        Assert.Equal(11_818.18m, run.Metrics.FinalEquity);
        Assert.Equal(1, run.Metrics.TradeCount);
        Assert.Equal(100m, run.Metrics.WinRatePercent);
        Assert.Equal(18.18m, run.Metrics.AverageTradeReturnPercent);
        Assert.Equal(50m, run.Metrics.ExposurePercent);
        Assert.False(run.Metrics.HasOpenPosition);
    }

    [Fact]
    public void The_strategy_only_ever_sees_bars_up_to_the_one_just_closed()
    {
        var bars = Closes(1m, 2m, 3m, 4m, 5m);
        var strategy = Script();

        BacktestEngine.Run(bars, strategy, NoCosts);

        // Fed once per bar, in order: at bar t it has seen exactly bars 0..t.
        Assert.Equal(bars.Select(x => x.Date), strategy.Seen);
    }

    [Fact]
    public void Changing_later_bars_does_not_change_earlier_trades()
    {
        var strategy = new MovingAverageCrossoverStrategy(2, 3, MovingAverageType.Sma);
        var closes = new[] { 10m, 10m, 10m, 13m, 7m, 7m, 12m, 15m, 9m, 8m };

        var full = BacktestEngine.Run(Closes(closes), strategy, NoCosts);

        var changed = closes.ToArray();
        changed[^1] = 30m;
        changed[^2] = 30m;
        var altered = BacktestEngine.Run(Closes(changed), strategy, NoCosts);

        var cutoff = Start.AddDays(closes.Length - 2);
        Assert.Equal(
            full.Trades.Where(x => x.ExitDate < cutoff && !x.IsOpen),
            altered.Trades.Where(x => x.ExitDate < cutoff && !x.IsOpen));
        Assert.Equal(full.Equity.Take(closes.Length - 2), altered.Equity.Take(closes.Length - 2));
    }

    [Fact]
    public void A_signal_on_the_last_bar_is_dropped()
    {
        var bars = Closes(10m, 11m, 12m);

        var run = BacktestEngine.Run(bars, Script((2, TradeSignal.Buy)), NoCosts);

        Assert.Empty(run.Trades);
        Assert.All(run.Equity, x => Assert.Equal(10_000m, x));
        Assert.Equal(0m, run.Metrics.TotalReturnPercent);
        Assert.Equal(0m, run.Metrics.ExposurePercent);
    }

    [Fact]
    public void Signals_during_warm_up_are_ignored()
    {
        var bars = Closes(10m, 11m, 12m, 13m, 14m);
        var strategy = new ScriptedStrategy(
            new Dictionary<int, TradeSignal>
            {
                [0] = TradeSignal.Buy,
                [1] = TradeSignal.Buy,
                [2] = TradeSignal.Buy
            },
            warmupBars: 2);

        var run = BacktestEngine.Run(bars, strategy, NoCosts);

        // Bar 2 is the first after warm-up; it fills at bar 3's open.
        var trade = Assert.Single(run.Trades);
        Assert.Equal(Start.AddDays(3), trade.EntryDate);
        Assert.Equal(13m, trade.EntryPrice);
    }

    [Fact]
    public void Buy_while_long_and_sell_while_flat_are_ignored()
    {
        var bars = Closes(10m, 10m, 10m, 10m, 10m);

        var run = BacktestEngine.Run(
            bars,
            Script((0, TradeSignal.Sell), (1, TradeSignal.Buy), (2, TradeSignal.Buy), (3, TradeSignal.Sell)),
            NoCosts);

        var trade = Assert.Single(run.Trades);
        Assert.Equal(Start.AddDays(2), trade.EntryDate);
        Assert.Equal(Start.AddDays(4), trade.ExitDate);
    }

    [Fact]
    public void Commission_and_slippage_apply_to_both_fills()
    {
        // Buy fills at 100 × 1.005 = 100.5; sell at 110 × 0.995 = 109.45.
        var bars = OpenClose((100m, 100m), (100m, 105m), (110m, 110m));
        var costs = new BacktestCosts(10_000m, CommissionPerTrade: 10m, CommissionPercent: 0.1m, SlippageBps: 50m);

        var run = BacktestEngine.Run(bars, Script((0, TradeSignal.Buy), (1, TradeSignal.Sell)), costs);

        var trade = Assert.Single(run.Trades);
        Assert.Equal(100.5m, trade.EntryPrice);
        Assert.Equal(109.45m, trade.ExitPrice);
        // (10,000 − 10) / (100.5 × 1.001)
        Assert.Equal(99.303681m, trade.Shares);
        // 99.3037 × 109.45 = 10,868.79, less 10 + 0.1% = 10,847.92 back.
        Assert.Equal(847.92m, trade.ProfitLoss);
        Assert.Equal(8.48m, trade.ReturnPercent);
        Assert.Equal(10_847.92m, run.Metrics.FinalEquity);

        // Held at bar 1's close: the position's value only (the commission is spent).
        Assert.Equal(10_426.89m, Math.Round(run.Equity[1], 2));
    }

    [Fact]
    public void A_fixed_commission_larger_than_the_cash_means_no_buy()
    {
        var bars = Closes(10m, 11m, 12m);
        var costs = new BacktestCosts(100m, CommissionPerTrade: 100m);

        var run = BacktestEngine.Run(bars, Script((0, TradeSignal.Buy)), costs);

        Assert.Empty(run.Trades);
        Assert.Equal(100m, run.Metrics.FinalEquity);
    }

    [Fact]
    public void An_open_position_at_the_end_is_marked_to_the_last_close_and_reported_open()
    {
        var bars = OpenClose((10m, 10m), (10m, 11m), (11m, 12m), (12m, 12.5m));
        var costs = new BacktestCosts(10_000m, CommissionPerTrade: 5m);

        var run = BacktestEngine.Run(bars, Script((0, TradeSignal.Buy)), costs);

        var trade = Assert.Single(run.Trades);
        Assert.True(trade.IsOpen);
        Assert.Equal(Start.AddDays(3), trade.ExitDate);
        Assert.Equal(12.5m, trade.ExitPrice);
        // 999.5 shares at 12.5, no exit commission: 12,493.75 − 10,000.
        Assert.Equal(999.5m, trade.Shares);
        Assert.Equal(2493.75m, trade.ProfitLoss);
        Assert.Equal(24.94m, trade.ReturnPercent);

        Assert.True(run.Metrics.HasOpenPosition);
        Assert.Equal(0, run.Metrics.TradeCount);
        Assert.Null(run.Metrics.WinRatePercent);
        Assert.Null(run.Metrics.AverageTradeReturnPercent);
        Assert.Equal(12_493.75m, run.Metrics.FinalEquity);
        Assert.Equal(75m, run.Metrics.ExposurePercent);
    }

    [Fact]
    public void Win_rate_and_average_return_cover_closed_trades()
    {
        // +10% (100 → 110), then −10% (110 → 99).
        var bars = OpenClose((100m, 100m), (100m, 100m), (110m, 110m), (110m, 110m), (99m, 99m));

        var run = BacktestEngine.Run(
            bars,
            Script((0, TradeSignal.Buy), (1, TradeSignal.Sell), (2, TradeSignal.Buy), (3, TradeSignal.Sell)),
            NoCosts);

        Assert.Equal(2, run.Metrics.TradeCount);
        Assert.Equal([10m, -10m], run.Trades.Select(x => x.ReturnPercent));
        Assert.Equal(50m, run.Metrics.WinRatePercent);
        Assert.Equal(0m, run.Metrics.AverageTradeReturnPercent);
        Assert.Equal(-1m, run.Metrics.TotalReturnPercent);
    }

    [Fact]
    public void Max_drawdown_is_the_largest_fall_from_a_closing_peak()
    {
        // Held from bar 1's open at 100: equity 10,000 → 12,000 → 9,000 → 13,000 → 10,400.
        var bars = OpenClose((100m, 100m), (100m, 120m), (120m, 90m), (90m, 130m), (130m, 104m));

        var run = BacktestEngine.Run(bars, Script((0, TradeSignal.Buy)), NoCosts);

        // 12,000 → 9,000 is 25%; 13,000 → 10,400 is only 20%.
        Assert.Equal(25m, run.Metrics.MaxDrawdownPercent);
        Assert.Equal(Start.AddDays(1), run.Metrics.MaxDrawdownPeakDate);
        Assert.Equal(Start.AddDays(2), run.Metrics.MaxDrawdownTroughDate);
    }

    [Fact]
    public void No_drawdown_reports_zero_and_no_dates()
    {
        var run = BacktestEngine.Run(Closes(10m, 11m, 12m), Script((0, TradeSignal.Buy)), NoCosts);

        Assert.Equal(0m, run.Metrics.MaxDrawdownPercent);
        Assert.Null(run.Metrics.MaxDrawdownPeakDate);
        Assert.Null(run.Metrics.MaxDrawdownTroughDate);
    }

    [Fact]
    public void Sharpe_annualizes_daily_returns_with_252_and_no_risk_free_rate()
    {
        // Equity 10,000, 10,000, 11,000, 9,900, 10,890: returns 0, +10%, −10%, +10%.
        // Mean 0.025, sample sd 0.0957427, × √252 = 4.1451.
        var bars = OpenClose((100m, 100m), (100m, 100m), (100m, 110m), (110m, 99m), (99m, 108.9m));

        var run = BacktestEngine.Run(bars, Script((0, TradeSignal.Buy)), NoCosts);

        Assert.Equal(4.15m, run.Metrics.SharpeRatio);
    }

    [Fact]
    public void Sharpe_is_null_for_a_flat_equity_curve()
    {
        var run = BacktestEngine.Run(Closes(10m, 11m, 12m, 13m), Script(), NoCosts);

        Assert.Null(run.Metrics.SharpeRatio);
    }

    [Fact]
    public void Cagr_compounds_over_calendar_years()
    {
        // 4 years (1,461 days) for +46.41% is 10% a year.
        var bars = new List<BacktestBar>
        {
            new(new DateOnly(2020, 1, 1), 100m, 100m, 100m, 100m),
            new(new DateOnly(2020, 1, 2), 100m, 100m, 100m, 100m),
            new(new DateOnly(2024, 1, 1), 146.41m, 146.41m, 146.41m, 146.41m)
        };

        var run = BacktestEngine.Run(bars, BuyAndHoldStrategy.Instance, NoCosts);

        Assert.Equal(46.41m, run.Metrics.TotalReturnPercent);
        Assert.Equal(10m, run.Metrics.CagrPercent);
    }

    [Fact]
    public void Buy_and_hold_buys_at_the_second_open_and_holds_to_the_end()
    {
        var bars = OpenClose((50m, 50m), (40m, 45m), (45m, 60m));

        var run = BacktestEngine.Run(bars, BuyAndHoldStrategy.Instance, NoCosts);

        var trade = Assert.Single(run.Trades);
        Assert.Equal(Start.AddDays(1), trade.EntryDate);
        Assert.Equal(40m, trade.EntryPrice);
        Assert.True(trade.IsOpen);
        Assert.Equal(50m, run.Metrics.TotalReturnPercent);
        // +50% in two days annualizes past any meaningful (or representable) rate.
        Assert.Null(run.Metrics.CagrPercent);
    }

    [Fact]
    public void The_calculator_runs_the_strategy_and_the_benchmark_over_the_same_bars()
    {
        var bars = OpenClose((10m, 10m), (10m, 11m), (11m, 12m), (12m, 9m));

        var result = BacktestCalculator.Calculate(bars, Script(), NoCosts);

        Assert.Empty(result.Strategy.Trades);
        Assert.Equal(0m, result.Strategy.Metrics.TotalReturnPercent);
        Assert.Equal(-10m, result.Benchmark.Metrics.TotalReturnPercent);

        Assert.Equal(bars.Select(x => x.Date), result.EquityCurve.Select(x => x.Date));
        Assert.All(result.EquityCurve, x => Assert.Equal(10_000m, x.Strategy));
        Assert.Equal([10_000m, 11_000m, 12_000m, 9_000m], result.EquityCurve.Select(x => x.Benchmark));
    }
}
