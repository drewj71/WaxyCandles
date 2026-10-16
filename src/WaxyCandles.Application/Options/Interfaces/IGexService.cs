using WaxyCandles.Application.Options.Gex;

namespace WaxyCandles.Application.Options.Interfaces;

public interface IGexService
{
    /// <summary>
    /// The GEX/VEX table for a symbol, or null when there's no quote or option
    /// data for it. Throws <see cref="Common.Exceptions.RequestValidationException"/>
    /// for an invalid request.
    /// </summary>
    Task<GexTable?> GetGexTableAsync(
        string symbol,
        GexRequest request,
        CancellationToken cancellationToken = default);
}
