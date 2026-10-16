namespace WaxyCandles.Application.Options.Models;

/// <param name="UnderlyingPrice">The underlying's price at <paramref name="AsOf"/>.</param>
/// <param name="AsOf">When the provider took the snapshot.</param>
public sealed record OptionChain(
    string Underlying,
    decimal UnderlyingPrice,
    DateTimeOffset AsOf,
    IReadOnlyList<OptionContract> Contracts);
