namespace WaxyCandles.Application.Backtesting;

/// <summary>One daily candle, by trading date.</summary>
public sealed record BacktestBar(
    DateOnly Date,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume = 0);
