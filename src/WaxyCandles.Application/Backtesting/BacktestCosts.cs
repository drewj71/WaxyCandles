namespace WaxyCandles.Application.Backtesting;

/// <param name="InitialCapital">Starting cash.</param>
/// <param name="CommissionPerTrade">Fixed commission on every fill (each buy and each sell).</param>
/// <param name="CommissionPercent">Commission as a percentage of each fill's value (0.1 = 0.1%).</param>
/// <param name="SlippageBps">
/// Basis points (1 bp = 0.01%) the fill is worse than the open: buys fill
/// above it, sells below.
/// </param>
public sealed record BacktestCosts(
    decimal InitialCapital = BacktestCosts.DefaultInitialCapital,
    decimal CommissionPerTrade = 0m,
    decimal CommissionPercent = 0m,
    decimal SlippageBps = 0m)
{
    public const decimal DefaultInitialCapital = 10_000m;
}
