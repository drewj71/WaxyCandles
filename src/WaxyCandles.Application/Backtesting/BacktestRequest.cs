using WaxyCandles.Application.Backtesting.Strategies;

namespace WaxyCandles.Application.Backtesting;

public enum BacktestStrategyType
{
    MovingAverageCrossover,
    RsiMeanReversion,
    Breakout
}

/// <summary>
/// POST /api/backtests. Only the chosen strategy's parameters are read; an
/// omitted parameter object (or field) takes its default. Costs default to
/// none and $10,000 starting capital.
/// </summary>
public sealed class BacktestRequest
{
    public string Symbol { get; set; } = "";

    /// <summary>First trading date to test (inclusive).</summary>
    public DateOnly? From { get; set; }

    /// <summary>Last trading date to test (inclusive); a future date means "up to now".</summary>
    public DateOnly? To { get; set; }

    public BacktestStrategyType Strategy { get; set; } = BacktestStrategyType.MovingAverageCrossover;

    public MovingAverageCrossoverParameters? MovingAverageCrossover { get; set; }

    public RsiMeanReversionParameters? RsiMeanReversion { get; set; }

    public BreakoutParameters? Breakout { get; set; }

    public decimal InitialCapital { get; set; } = BacktestCosts.DefaultInitialCapital;

    /// <summary>Fixed commission on each buy and each sell.</summary>
    public decimal CommissionPerTrade { get; set; }

    /// <summary>Commission as a percentage of each fill's value (0.1 = 0.1%).</summary>
    public decimal CommissionPercent { get; set; }

    /// <summary>Slippage in basis points against each fill.</summary>
    public decimal SlippageBps { get; set; }
}

public sealed class MovingAverageCrossoverParameters
{
    public int FastPeriod { get; set; } = 50;

    public int SlowPeriod { get; set; } = 200;

    public MovingAverageType MovingAverageType { get; set; } = MovingAverageType.Sma;
}

public sealed class RsiMeanReversionParameters
{
    public int Period { get; set; } = 14;

    public decimal Oversold { get; set; } = 30m;

    public decimal Overbought { get; set; } = 70m;
}

/// <summary>Donchian channel lengths, in bars.</summary>
public sealed class BreakoutParameters
{
    public int EntryPeriod { get; set; } = 20;

    public int ExitPeriod { get; set; } = 10;
}
