using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using WaxyCandles.Application.Options.Interfaces;
using WaxyCandles.Application.Options.Models;

namespace WaxyCandles.Infrastructure.MarketData.Providers;

/// <summary>
/// Option chains from Market Data (marketdata.app) options/chain.
/// Billing depends on the data mode: live or delayed chains cost 1 credit per
/// contract returned, cached chains 1 credit per request (paid plans only), and
/// trial/free accounts get the previous session, where Greeks and IV may be null.
/// <c>MarketDataApp:OptionsMode</c> sets the mode; blank leaves it to the
/// account's default (the only choice on a trial).
/// </summary>
public class MarketDataAppOptionsDataProvider : IOptionsDataProvider
{
    private static readonly string[] Modes = ["live", "delayed", "cached"];

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public MarketDataAppOptionsDataProvider(
        HttpClient httpClient,
        IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task<OptionChain?> GetOptionChainAsync(
        OptionChainQuery query,
        CancellationToken cancellationToken = default)
    {
        var symbol = query.Symbol.Trim().ToUpperInvariant();

        var url =
            $"{MarketDataAppApi.BaseUrl}/options/chain/{Uri.EscapeDataString(symbol)}/" +
            $"?from={query.ExpirationFrom:yyyy-MM-dd}" +
            $"&to={query.ExpirationTo:yyyy-MM-dd}" +
            $"&strike={Format(query.StrikeMin)}-{Format(query.StrikeMax)}";

        var mode = _configuration["MarketDataApp:OptionsMode"];

        if (!string.IsNullOrWhiteSpace(mode))
        {
            if (!Modes.Contains(mode))
            {
                throw new InvalidOperationException(
                    $"Unknown MarketDataApp:OptionsMode \"{mode}\". " +
                    "Expected \"live\", \"delayed\", \"cached\" or blank.");
            }

            url += $"&mode={mode}";
        }

        // Null for no data: HTTP 404, s = "no_data", or (cached mode) a cache
        // miss, HTTP 204.
        using var document = await MarketDataAppApi.GetAsync(
            _httpClient,
            _configuration,
            url,
            cancellationToken);

        if (document is null)
        {
            return null;
        }

        var root = document.RootElement;

        var optionSymbols = root.GetProperty("optionSymbol");
        var sides = root.GetProperty("side");
        var strikes = root.GetProperty("strike");
        var expirations = root.GetProperty("expiration");
        var openInterests = root.GetProperty("openInterest");
        var ivs = root.GetProperty("iv");
        var gammas = root.GetProperty("gamma");
        var mids = root.GetProperty("mid");
        var underlyingPrices = root.GetProperty("underlyingPrice");
        var updated = root.GetProperty("updated");

        var contracts = new List<OptionContract>();
        long? latestUpdate = null;
        decimal underlyingPrice = 0;

        for (var i = 0; i < optionSymbols.GetArrayLength(); i++)
        {
            OptionSide? side = sides[i].GetString() switch
            {
                "call" => OptionSide.Call,
                "put" => OptionSide.Put,
                _ => null
            };

            if (side is null ||
                strikes[i].ValueKind != JsonValueKind.Number ||
                expirations[i].ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            // The underlying price that goes with the newest snapshot.
            if (updated[i].ValueKind == JsonValueKind.Number &&
                underlyingPrices[i].ValueKind == JsonValueKind.Number &&
                updated[i].GetInt64() >= (latestUpdate ?? long.MinValue))
            {
                latestUpdate = updated[i].GetInt64();
                underlyingPrice = underlyingPrices[i].GetDecimal();
            }

            contracts.Add(new OptionContract(
                optionSymbols[i].GetString()!,
                side.Value,
                strikes[i].GetDecimal(),
                DateTimeOffset.FromUnixTimeSeconds(expirations[i].GetInt64()),
                openInterests[i].ValueKind == JsonValueKind.Number ? openInterests[i].GetInt64() : 0,
                NullableDouble(ivs[i]),
                NullableDouble(gammas[i]),
                mids[i].ValueKind == JsonValueKind.Number ? mids[i].GetDecimal() : null));
        }

        if (contracts.Count == 0 || latestUpdate is null || underlyingPrice <= 0)
        {
            return null;
        }

        return new OptionChain(
            symbol,
            underlyingPrice,
            DateTimeOffset.FromUnixTimeSeconds(latestUpdate.Value),
            contracts);
    }

    private static double? NullableDouble(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number ? element.GetDouble() : null;

    private static string Format(decimal value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);
}
