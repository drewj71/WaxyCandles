namespace WaxyCandles.Application.Options.Models;

/// <summary>
/// One option contract in a chain snapshot. Greeks and IV are null when the
/// provider doesn't supply them (e.g. Market Data's historical chains).
/// </summary>
public sealed record OptionContract(
    string OptionSymbol,
    OptionSide Side,
    decimal Strike,
    DateTimeOffset Expiration,
    long OpenInterest,
    double? ImpliedVolatility,
    double? Gamma,
    decimal? Mid);
