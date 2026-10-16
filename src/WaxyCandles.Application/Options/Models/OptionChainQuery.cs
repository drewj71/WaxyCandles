namespace WaxyCandles.Application.Options.Models;

/// <summary>
/// The slice of a chain to fetch. Providers that bill per contract charge for
/// everything returned, so both ranges should be as narrow as the view needs.
/// </summary>
/// <param name="ExpirationFrom">First expiration date (inclusive).</param>
/// <param name="ExpirationTo">Last expiration date (inclusive).</param>
/// <param name="StrikeMin">Lowest strike (inclusive).</param>
/// <param name="StrikeMax">Highest strike (inclusive).</param>
public sealed record OptionChainQuery(
    string Symbol,
    DateOnly ExpirationFrom,
    DateOnly ExpirationTo,
    decimal StrikeMin,
    decimal StrikeMax);
