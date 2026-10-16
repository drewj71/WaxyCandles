using WaxyCandles.Application.Options.Gex;

namespace WaxyCandles.Application.Alerts.Levels;

/// <summary>A symbol's GEX levels, with the price to resolve them against.</summary>
/// <param name="Levels">The board's GEX (not VEX) levels.</param>
/// <param name="AsOf">The option chain snapshot time.</param>
/// <param name="CurrentPrice">The latest quote, which decides which side of a level the price is on.</param>
public sealed record GexLevelBoard(
    GexLevels Levels,
    DateTimeOffset AsOf,
    decimal CurrentPrice);
