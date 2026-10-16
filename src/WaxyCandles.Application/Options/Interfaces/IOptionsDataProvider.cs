using WaxyCandles.Application.Options.Models;

namespace WaxyCandles.Application.Options.Interfaces;

/// <summary>
/// Option chain data, kept apart from <see cref="MarketData.Interfaces.IMarketDataProvider"/>
/// because not every stock data vendor has options.
/// </summary>
public interface IOptionsDataProvider
{
    /// <summary>The chain slice, or null when the provider has no data for it.</summary>
    Task<OptionChain?> GetOptionChainAsync(
        OptionChainQuery query,
        CancellationToken cancellationToken = default);
}
