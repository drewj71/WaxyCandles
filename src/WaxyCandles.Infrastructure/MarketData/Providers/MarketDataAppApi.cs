using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace WaxyCandles.Infrastructure.MarketData.Providers;

/// <summary>Request and error handling shared by the Market Data API clients.</summary>
internal static class MarketDataAppApi
{
    public const string BaseUrl = "https://api.marketdata.app/v1";

    /// <summary>
    /// Sends an authenticated GET. Returns null when Market Data has no data for
    /// the request (HTTP 404 or s = "no_data"); throws on any other failure.
    /// </summary>
    public static async Task<JsonDocument?> GetAsync(
        HttpClient httpClient,
        IConfiguration configuration,
        string url,
        CancellationToken cancellationToken)
    {
        var token = configuration["MarketDataApp:ApiToken"];

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "Market Data API token is not configured (MarketDataApp:ApiToken).");
        }

        // Header rather than ?token=, so the token never ends up in logged URLs.
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(
            request,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(
            cancellationToken);

        // 2xx includes 203, which Market Data uses for answers from its cache.
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Market Data error ({(int)response.StatusCode}): {ErrorMessage(json)}");
        }

        var document = JsonDocument.Parse(json);

        var status = document.RootElement.TryGetProperty("s", out var s)
            ? s.GetString()
            : null;

        if (status == "ok")
        {
            return document;
        }

        document.Dispose();

        if (status == "no_data")
        {
            return null;
        }

        throw new InvalidOperationException(
            $"Market Data error: {ErrorMessage(json)}");
    }

    private static string ErrorMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.TryGetProperty("errmsg", out var errmsg))
            {
                return errmsg.GetString() ?? "unknown error";
            }
        }
        catch (JsonException)
        {
            // Gateway errors (5xx) can come back as HTML.
        }

        return "unknown error";
    }
}
