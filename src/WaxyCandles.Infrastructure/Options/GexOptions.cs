namespace WaxyCandles.Infrastructure.Options;

/// <summary>The optional <c>Gex</c> configuration section.</summary>
public sealed class GexOptions
{
    public const string SectionName = "Gex";

    /// <summary>
    /// How long a computed table is reused for the same symbol and ranges.
    /// Each miss costs a quote and a chain request.
    /// </summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Annual risk-free rate (0.04 = 4%) for the Black-Scholes vanna, and for
    /// gamma when the provider doesn't supply it. GEX barely moves with it.
    /// </summary>
    public double RiskFreeRate { get; set; } = 0.04;
}
