namespace WaxyCandles.Application.Options.Gex;

/// <param name="Days">Expirations from today up to this many calendar days out (0 = today only).</param>
/// <param name="StrikeRangePercent">Strikes within this percentage of the underlying price, either side.</param>
public sealed record GexRequest(
    int Days = GexRequest.DefaultDays,
    decimal StrikeRangePercent = GexRequest.DefaultStrikeRangePercent)
{
    public const int DefaultDays = 30;
    public const int MaxDays = 365;
    public const decimal DefaultStrikeRangePercent = 10m;
    public const decimal MaxStrikeRangePercent = 50m;
}
