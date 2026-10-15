using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using WaxyCandles.Application.MarketData.Interfaces;
using WaxyCandles.Application.MarketData.Models;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;

namespace WaxyCandles.Infrastructure.MarketData.Providers;

/// <summary>
/// Market Data (marketdata.app) REST API. Every response field is an array, even
/// for a single symbol, and times are Unix seconds.
/// </summary>
public class MarketDataAppMarketDataProvider : IMarketDataProvider
{
    private const string BaseUrl = "https://api.marketdata.app/v1";

    // How far back an import reaches. Intraday is capped at 1 year per request
    // by the API; candles cost 1 credit per 1,000, so these are cheap.
    private static readonly TimeSpan IntradayLookback = TimeSpan.FromDays(30);
    private static readonly TimeSpan DailyLookback = TimeSpan.FromDays(365);

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public MarketDataAppMarketDataProvider(
        HttpClient httpClient,
        IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task<MarketQuote?> GetCurrentQuoteAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var normalizedSymbol =
            symbol.Trim().ToUpperInvariant();

        // stocks/prices, not stocks/quotes: quotes are delayed or a day old unless
        // the account has the exchange agreements, while prices are real-time on
        // every plan. The price is a SmartMid (bid/ask midpoint), not the last
        // trade, and the response has no volume.
        using var document = await GetAsync(
            $"{BaseUrl}/stocks/prices/{Uri.EscapeDataString(normalizedSymbol)}/",
            cancellationToken);

        // Unknown symbol: HTTP 404 or s = "no_data".
        if (document is null)
        {
            return null;
        }

        var root = document.RootElement;

        var price = First(root, "mid").GetDecimal();
        var change = First(root, "change").GetDecimal();

        return new MarketQuote
        {
            Symbol = normalizedSymbol,
            Price = price,
            PreviousClose = price - change,
            Change = change,
            // Market Data returns a fraction (0.0328); the app stores percent (3.28).
            ChangePercent = First(root, "changepct").GetDecimal() * 100m,
            LatestTradingDay = ToTradingDate(First(root, "updated").GetInt64())
        };
    }

    public async Task<IReadOnlyList<StockCandle>> GetHistoricalCandlesAsync(
        string symbol,
        CandleInterval interval,
        CancellationToken cancellationToken = default)
    {
        var normalizedSymbol =
            symbol.Trim().ToUpperInvariant();

        var resolution = interval switch
        {
            CandleInterval.FiveMinutes => "5",
            CandleInterval.FifteenMinutes => "15",
            CandleInterval.ThirtyMinutes => "30",
            CandleInterval.OneHour => "1H",
            CandleInterval.OneDay => "D",

            _ => throw new ArgumentOutOfRangeException(
                nameof(interval))
        };

        var to = DateTimeOffset.UtcNow;
        var from = to - (interval == CandleInterval.OneDay
            ? DailyLookback
            : IntradayLookback);

        using var document = await GetAsync(
            $"{BaseUrl}/stocks/candles/{resolution}/{Uri.EscapeDataString(normalizedSymbol)}/" +
            $"?from={from.ToUnixTimeSeconds()}" +
            $"&to={to.ToUnixTimeSeconds()}",
            cancellationToken);

        if (document is null)
        {
            return [];
        }

        var root = document.RootElement;

        var times = root.GetProperty("t");
        var opens = root.GetProperty("o");
        var highs = root.GetProperty("h");
        var lows = root.GetProperty("l");
        var closes = root.GetProperty("c");
        var volumes = root.GetProperty("v");

        var candles = new List<StockCandle>();

        for (var i = 0; i < times.GetArrayLength(); i++)
        {
            var time = times[i].GetInt64();

            candles.Add(new StockCandle
            {
                Id = Guid.NewGuid(),
                Symbol = normalizedSymbol,
                Interval = interval,
                // Daily candles are stamped midnight US/Eastern; store them as the
                // trading date (UTC midnight), the same as the Alpha Vantage provider,
                // so candle dedup by timestamp works whichever provider imported them.
                Timestamp = interval == CandleInterval.OneDay
                    ? ToTradingDate(time)
                    : DateTimeOffset.FromUnixTimeSeconds(time),
                Open = opens[i].GetDecimal(),
                High = highs[i].GetDecimal(),
                Low = lows[i].GetDecimal(),
                Close = closes[i].GetDecimal(),
                Volume = volumes[i].GetInt64()
            });
        }

        return candles
            .OrderBy(x => x.Timestamp)
            .ToList();
    }

    /// <summary>
    /// Sends an authenticated GET. Returns null when Market Data has no data for
    /// the request (HTTP 404 or s = "no_data"); throws on any other failure.
    /// </summary>
    private async Task<JsonDocument?> GetAsync(
        string url,
        CancellationToken cancellationToken)
    {
        var token = _configuration["MarketDataApp:ApiToken"];

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "Market Data API token is not configured (MarketDataApp:ApiToken).");
        }

        // Header rather than ?token=, so the token never ends up in logged URLs.
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(
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

    private static JsonElement First(JsonElement root, string property) =>
        root.GetProperty(property)[0];

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

    private static readonly TimeZoneInfo MarketTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>
    /// The US/Eastern trading date of a Unix time, as UTC midnight of that date,
    /// so it is the same value whichever machine parses it.
    /// </summary>
    private static DateTimeOffset ToTradingDate(long unixSeconds)
    {
        var eastern = TimeZoneInfo.ConvertTime(
            DateTimeOffset.FromUnixTimeSeconds(unixSeconds),
            MarketTimeZone);

        return new DateTimeOffset(
            eastern.Date,
            TimeSpan.Zero);
    }
}
