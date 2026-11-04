using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using WaxyCandles.Application.MarketData.Exceptions;
using WaxyCandles.Infrastructure.Telemetry;

namespace WaxyCandles.Infrastructure.MarketData.Providers;

/// <summary>Request and error handling shared by the Market Data API clients.</summary>
internal static class MarketDataAppApi
{
    public const string BaseUrl = "https://api.marketdata.app/v1";

    /// <summary>The vendor name in MarketDataUnavailableException.</summary>
    public const string ProviderName = "Market Data";

    /// <summary>
    /// Named HttpClients, each with its timeout (HttpClientTimeouts): stock
    /// prices, candles and the market calendar share one; option chains,
    /// which are larger, have their own.
    /// </summary>
    public const string HttpClientName = "MarketDataApp";
    public const string OptionsHttpClientName = "MarketDataApp.Options";

    // Usage headers on every response (docs/api/rate-limiting): the credits
    // this request cost, and what's left in the current window (a day on
    // Free/Starter/Trader, resetting 9:30 ET; a minute on Quant/Prime).
    public const string CreditsConsumedHeader = "X-Api-Ratelimit-Consumed";
    public const string CreditsRemainingHeader = "X-Api-Ratelimit-Remaining";

    // When the current window resets, as UTC epoch seconds.
    public const string RateLimitResetHeader = "X-Api-Ratelimit-Reset";

    /// <summary>Endpoint kinds, the waxycandles.marketdata.* "endpoint" tag.</summary>
    public static class Endpoints
    {
        public const string Prices = "prices";
        public const string Candles = "candles";
        public const string OptionChain = "option_chain";
        public const string MarketStatus = "market_status";
        public const string Other = "other";
    }

    /// <summary>
    /// Sends an authenticated GET. Returns null when Market Data has no data for
    /// the request (HTTP 404, s = "no_data", or HTTP 204, a cached-mode miss);
    /// throws on any other failure: MarketDataUnavailableException for a
    /// timeout or a 429 (credits spent), InvalidOperationException otherwise.
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

        var endpoint = EndpointOf(url);
        var outcome = "error";

        try
        {
            var document = await SendAsync(
                httpClient,
                token,
                url,
                endpoint,
                cancellationToken);

            outcome = document is null ? "no_data" : "ok";
            return document;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            outcome = "cancelled";
            throw;
        }
        // HttpClient.Timeout. Never retried: a response that was already
        // being sent may have been billed. The caller sees the exception (the
        // poller logs it and polls again on schedule; the API returns 504).
        catch (TaskCanceledException ex)
            when (ex.InnerException is TimeoutException)
        {
            outcome = "timeout";
            throw new MarketDataUnavailableException(
                MarketDataUnavailableReason.Timeout,
                ProviderName,
                innerException: ex);
        }
        finally
        {
            WaxyCandlesMetrics.MarketDataRequests.Add(
                1,
                new KeyValuePair<string, object?>(WaxyCandlesMetrics.EndpointTag, endpoint),
                new KeyValuePair<string, object?>(WaxyCandlesMetrics.OutcomeTag, outcome));
        }
    }

    private static async Task<JsonDocument?> SendAsync(
        HttpClient httpClient,
        string token,
        string url,
        string endpoint,
        CancellationToken cancellationToken)
    {
        // Header rather than ?token=, so the token never ends up in logged URLs.
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        using var response = await httpClient.SendAsync(
            request,
            cancellationToken);

        RecordCredits(response, endpoint);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
        {
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(
            cancellationToken);

        // Credits spent (or, on Quant/Prime, the per-minute limit). Not retried;
        // the API returns 503 with the vendor's wait, if it gave one.
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new MarketDataUnavailableException(
                MarketDataUnavailableReason.RateLimited,
                ProviderName,
                RetryAfter(response, DateTimeOffset.UtcNow));
        }

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

    /// <summary>The endpoint kind of a request URL. Never the symbol or other path segments.</summary>
    internal static string EndpointOf(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath
            : url;

        return path switch
        {
            _ when path.Contains("/stocks/prices/", StringComparison.OrdinalIgnoreCase) => Endpoints.Prices,
            _ when path.Contains("/stocks/candles/", StringComparison.OrdinalIgnoreCase) => Endpoints.Candles,
            _ when path.Contains("/options/chain/", StringComparison.OrdinalIgnoreCase) => Endpoints.OptionChain,
            _ when path.Contains("/markets/status/", StringComparison.OrdinalIgnoreCase) => Endpoints.MarketStatus,
            _ => Endpoints.Other
        };
    }

    // Read whatever the status: Market Data says only 200/203 are charged,
    // and reports what it charged, so this records its own figure.
    private static void RecordCredits(HttpResponseMessage response, string endpoint)
    {
        if (HeaderLong(response, CreditsConsumedHeader) is { } consumed and > 0)
        {
            WaxyCandlesMetrics.MarketDataCredits.Add(
                consumed,
                new KeyValuePair<string, object?>(WaxyCandlesMetrics.EndpointTag, endpoint));
        }

        // Can go negative: one request may cost more than the balance left.
        if (HeaderLong(response, CreditsRemainingHeader) is { } remaining)
        {
            WaxyCandlesMetrics.SetMarketDataCreditsRemaining(remaining);
        }
    }

    /// <summary>
    /// How long a 429 says to wait: the standard Retry-After (seconds or a
    /// date), else the time until the X-Api-Ratelimit-Reset epoch. Null if
    /// neither is present or the moment has passed.
    /// </summary>
    internal static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var retryAfter = response.Headers.RetryAfter;

        var delay = retryAfter?.Delta
            ?? (retryAfter?.Date is { } date ? date - now : (TimeSpan?)null)
            ?? (HeaderLong(response, RateLimitResetHeader) is { } reset
                ? DateTimeOffset.FromUnixTimeSeconds(reset) - now
                : (TimeSpan?)null);

        return delay > TimeSpan.Zero ? delay : null;
    }

    private static long? HeaderLong(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) &&
        long.TryParse(
            values.FirstOrDefault(),
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;

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
