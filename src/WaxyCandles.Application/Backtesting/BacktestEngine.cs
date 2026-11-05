namespace WaxyCandles.Application.Backtesting;

/// <summary>
/// Runs one long-only strategy over daily bars. Pure and deterministic: no
/// I/O, no clock, decimal arithmetic except the square root and power in
/// Sharpe and CAGR.
/// <list type="bullet">
/// <item>A signal is computed on bar t's close and fills at bar t+1's open, so
/// a decision never sees the bar it trades on. A signal on the last bar has no
/// next bar and is dropped. Signals during the strategy's warm-up are ignored.</item>
/// <item>Sizing is all-in with fractional shares: a buy spends all cash, so
/// shares = (cash − fixed commission) / (fill × (1 + commission %)). A sell
/// closes the whole position.</item>
/// <item>A position still open after the last bar is marked at the last close
/// (no exit costs) and reported as open.</item>
/// </list>
/// </summary>
public static class BacktestEngine
{
    public const int TradingDaysPerYear = 252;
    private const decimal DaysPerYear = 365.25m;

    public static BacktestRun Run(
        IReadOnlyList<BacktestBar> bars,
        IBacktestStrategy strategy,
        BacktestCosts costs)
    {
        var source = strategy.Start();
        var equity = new decimal[bars.Count];
        var trades = new List<BacktestTrade>();
        var barsInPosition = 0;

        var cash = costs.InitialCapital;
        var shares = 0m;
        var pending = TradeSignal.None;

        // The open trade's entry.
        var entryDate = default(DateOnly);
        var entryFill = 0m;
        var entryCost = 0m;

        var slippage = costs.SlippageBps / 10_000m;
        var commissionRate = costs.CommissionPercent / 100m;

        for (var t = 0; t < bars.Count; t++)
        {
            var bar = bars[t];

            if (pending == TradeSignal.Buy && cash > costs.CommissionPerTrade && bar.Open > 0m)
            {
                var fill = bar.Open * (1m + slippage);

                shares = (cash - costs.CommissionPerTrade) / (fill * (1m + commissionRate));
                entryDate = bar.Date;
                entryFill = fill;
                // All of it: the shares' value plus the commission.
                entryCost = cash;
                cash = 0m;
            }
            else if (pending == TradeSignal.Sell)
            {
                var fill = bar.Open * (1m - slippage);
                var value = shares * fill;
                var proceeds = value - costs.CommissionPerTrade - value * commissionRate;

                trades.Add(Trade(entryDate, entryFill, bar.Date, fill, shares, proceeds - entryCost, entryCost, isOpen: false));

                cash += proceeds;
                shares = 0m;
            }

            pending = TradeSignal.None;
            equity[t] = cash + shares * bar.Close;

            if (shares > 0m)
            {
                barsInPosition++;
            }

            // Always called, so indicators see every bar including warm-up.
            var signal = source.OnBarClose(bar);

            if (t < strategy.WarmupBars || t == bars.Count - 1)
            {
                continue;
            }

            if ((signal == TradeSignal.Buy && shares == 0m) ||
                (signal == TradeSignal.Sell && shares > 0m))
            {
                pending = signal;
            }
        }

        if (shares > 0m)
        {
            var last = bars[^1];
            trades.Add(Trade(entryDate, entryFill, last.Date, last.Close, shares, shares * last.Close - entryCost, entryCost, isOpen: true));
        }

        return new BacktestRun(
            Metrics(bars, equity, trades, costs.InitialCapital, barsInPosition, shares > 0m),
            trades,
            equity);
    }

    private static BacktestTrade Trade(
        DateOnly entryDate,
        decimal entryPrice,
        DateOnly exitDate,
        decimal exitPrice,
        decimal shares,
        decimal profitLoss,
        decimal entryCost,
        bool isOpen) =>
        new(
            entryDate,
            Math.Round(entryPrice, 4, MidpointRounding.AwayFromZero),
            exitDate,
            Math.Round(exitPrice, 4, MidpointRounding.AwayFromZero),
            Math.Round(shares, 6, MidpointRounding.AwayFromZero),
            Money(profitLoss),
            Percent(entryCost == 0m ? 0m : profitLoss / entryCost),
            isOpen);

    private static BacktestMetrics Metrics(
        IReadOnlyList<BacktestBar> bars,
        decimal[] equity,
        List<BacktestTrade> trades,
        decimal initialCapital,
        int barsInPosition,
        bool hasOpenPosition)
    {
        if (bars.Count == 0)
        {
            return new BacktestMetrics(Money(initialCapital), 0m, null, 0m, null, null, null, 0, null, null, 0m, false);
        }

        var final = equity[^1];
        var growth = final / initialCapital;

        var days = bars[^1].Date.DayNumber - bars[0].Date.DayNumber;

        var (drawdown, peak, trough) = MaxDrawdown(bars, equity);

        var closed = trades.Where(x => !x.IsOpen).ToList();

        return new BacktestMetrics(
            Money(final),
            Percent(growth - 1m),
            Cagr(growth, days),
            Percent(drawdown),
            peak,
            trough,
            Sharpe(equity),
            closed.Count,
            closed.Count == 0 ? null : Percent((decimal)closed.Count(x => x.ProfitLoss > 0m) / closed.Count),
            closed.Count == 0 ? null : Math.Round(closed.Average(x => x.ReturnPercent), 2, MidpointRounding.AwayFromZero),
            Percent((decimal)barsInPosition / bars.Count),
            hasOpenPosition);
    }

    // Annualizing a few days can give an absurd (even unrepresentable) rate;
    // past this many percent it is reported as unknown.
    private const double MaxReportableCagrPercent = 1_000_000;

    private static decimal? Cagr(decimal growth, int days)
    {
        if (days <= 0)
        {
            return null;
        }

        if (growth <= 0m)
        {
            return -100m;
        }

        var rate = (Math.Pow((double)growth, (double)DaysPerYear / days) - 1.0) * 100.0;

        return double.IsFinite(rate) && rate <= MaxReportableCagrPercent
            ? Math.Round((decimal)rate, 2, MidpointRounding.AwayFromZero)
            : null;
    }

    private static (decimal Drawdown, DateOnly? Peak, DateOnly? Trough) MaxDrawdown(
        IReadOnlyList<BacktestBar> bars,
        decimal[] equity)
    {
        var peakIndex = 0;
        var maxDrawdown = 0m;
        DateOnly? peakDate = null;
        DateOnly? troughDate = null;

        for (var t = 1; t < equity.Length; t++)
        {
            if (equity[t] > equity[peakIndex])
            {
                peakIndex = t;
                continue;
            }

            if (equity[peakIndex] <= 0m)
            {
                continue;
            }

            var drawdown = (equity[peakIndex] - equity[t]) / equity[peakIndex];

            if (drawdown > maxDrawdown)
            {
                maxDrawdown = drawdown;
                peakDate = bars[peakIndex].Date;
                troughDate = bars[t].Date;
            }
        }

        return (maxDrawdown, peakDate, troughDate);
    }

    private static decimal? Sharpe(decimal[] equity)
    {
        var returns = new List<double>();

        for (var t = 1; t < equity.Length; t++)
        {
            if (equity[t - 1] > 0m)
            {
                returns.Add((double)(equity[t] / equity[t - 1] - 1m));
            }
        }

        if (returns.Count < 2)
        {
            return null;
        }

        var mean = returns.Average();
        var variance = returns.Sum(r => (r - mean) * (r - mean)) / (returns.Count - 1);
        var deviation = Math.Sqrt(variance);

        // Below this the series is flat up to rounding; a ratio would be noise.
        if (deviation < 1e-12)
        {
            return null;
        }

        return Math.Round((decimal)(mean / deviation * Math.Sqrt(TradingDaysPerYear)), 2, MidpointRounding.AwayFromZero);
    }

    private static decimal Money(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>A fraction (0.125) as a rounded percentage (12.5).</summary>
    private static decimal Percent(decimal fraction) =>
        Math.Round(fraction * 100m, 2, MidpointRounding.AwayFromZero);
}
