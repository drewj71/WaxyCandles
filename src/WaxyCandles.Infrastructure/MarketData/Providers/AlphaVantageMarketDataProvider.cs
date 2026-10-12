using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using WaxyCandles.Application.MarketData.Interfaces;
using WaxyCandles.Application.MarketData.Models;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;

namespace WaxyCandles.Infrastructure.MarketData.Providers;

public class AlphaVantageMarketDataProvider : IMarketDataProvider
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public AlphaVantageMarketDataProvider(
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
        var apiKey = _configuration["AlphaVantage:ApiKey"];

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "Alpha Vantage API key is not configured.");
        }

        var encodedSymbol =
            Uri.EscapeDataString(symbol.Trim().ToUpperInvariant());

        var url =
            $"https://www.alphavantage.co/query" +
            $"?function=GLOBAL_QUOTE" +
            $"&symbol={encodedSymbol}" +
            $"&apikey={apiKey}";

        var response = await _httpClient.GetAsync(
            url,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(
            cancellationToken);

        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty(
                "Global Quote",
                out var quote))
        {
            ThrowAlphaVantageError(
                document.RootElement,
                $"Alpha Vantage did not return quote data for {symbol}.");
        }

        // Alpha Vantage answers an unknown symbol with an empty "Global Quote".
        if (!quote.EnumerateObject().Any())
        {
            return null;
        }

        var price = decimal.Parse(
            quote.GetProperty("05. price").GetString()!,
            CultureInfo.InvariantCulture);

        var previousClose = decimal.Parse(
            quote.GetProperty("08. previous close").GetString()!,
            CultureInfo.InvariantCulture);

        var change = decimal.Parse(
            quote.GetProperty("09. change").GetString()!,
            CultureInfo.InvariantCulture);

        var changePercentText =
            quote.GetProperty("10. change percent")
                .GetString()!
                .Replace("%", "");

        var changePercent = decimal.Parse(
            changePercentText,
            CultureInfo.InvariantCulture);

        var volume = long.Parse(
            quote.GetProperty("06. volume").GetString()!,
            CultureInfo.InvariantCulture);

        var latestTradingDay =
            ParseTradingDate(
                quote.GetProperty("07. latest trading day").GetString()!);

        return new MarketQuote
        {
            Symbol = symbol.Trim().ToUpperInvariant(),
            Price = price,
            PreviousClose = previousClose,
            Change = change,
            ChangePercent = changePercent,
            Volume = volume,
            LatestTradingDay = latestTradingDay
        };
    }

    public async Task<IReadOnlyList<StockCandle>> GetHistoricalCandlesAsync(
        string symbol,
        CandleInterval interval,
        CancellationToken cancellationToken = default)
    {
        var apiKey = _configuration["AlphaVantage:ApiKey"];

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Alpha Vantage API key is not configured.");

        var normalizedSymbol =
            symbol.Trim().ToUpperInvariant();

        if (interval == CandleInterval.OneDay)
        {
            return await GetDailyCandlesAsync(
                normalizedSymbol,
                apiKey,
                cancellationToken);
        }

        var alphaVantageInterval = interval switch
        {
            CandleInterval.FiveMinutes => "5min",
            CandleInterval.FifteenMinutes => "15min",
            CandleInterval.ThirtyMinutes => "30min",
            CandleInterval.OneHour => "60min",

            _ => throw new ArgumentOutOfRangeException(
                nameof(interval))
        };

        return await GetIntradayCandlesAsync(
            normalizedSymbol,
            alphaVantageInterval,
            interval,
            apiKey,
            cancellationToken);
    }

    private async Task<IReadOnlyList<StockCandle>> GetDailyCandlesAsync(
        string symbol,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var encodedSymbol =
            Uri.EscapeDataString(symbol.Trim().ToUpperInvariant());

        var url =
            $"https://www.alphavantage.co/query" +
            $"?function=TIME_SERIES_DAILY" +
            $"&symbol={encodedSymbol}" +
            $"&outputsize=compact" +
            $"&apikey={apiKey}";

        var response = await _httpClient.GetAsync(
            url,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(
            cancellationToken);

        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty(
                "Time Series (Daily)",
                out var timeSeries))
        {
            ThrowAlphaVantageError(
                document.RootElement,
                $"Alpha Vantage did not return expected time series data for {symbol}.");
        }

        var candles = new List<StockCandle>();

        foreach (var day in timeSeries.EnumerateObject())
        {
            var date = ParseTradingDate(day.Name);

            var values = day.Value;

            var open = decimal.Parse(
                values.GetProperty("1. open").GetString()!,
                CultureInfo.InvariantCulture);

            var high = decimal.Parse(
                values.GetProperty("2. high").GetString()!,
                CultureInfo.InvariantCulture);

            var low = decimal.Parse(
                values.GetProperty("3. low").GetString()!,
                CultureInfo.InvariantCulture);

            var close = decimal.Parse(
                values.GetProperty("4. close").GetString()!,
                CultureInfo.InvariantCulture);

            var volume = long.Parse(
                values.GetProperty("5. volume").GetString()!,
                CultureInfo.InvariantCulture);

            candles.Add(new StockCandle
            {
                Id = Guid.NewGuid(),
                Symbol = symbol.Trim().ToUpperInvariant(),
                Interval = CandleInterval.OneDay,
                Timestamp = date,
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = volume
            });
        }

        return candles;
    }

    private async Task<IReadOnlyList<StockCandle>> GetIntradayCandlesAsync(
        string symbol,
        string interval,
        CandleInterval candleInterval,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var url =
            $"https://www.alphavantage.co/query" +
            $"?function=TIME_SERIES_INTRADAY" +
            $"&symbol={symbol}" +
            $"&interval={interval}" +
            $"&outputsize=compact" +
            $"&apikey={apiKey}";

        using var response =
            await _httpClient.GetAsync(
                url,
                cancellationToken);

        response.EnsureSuccessStatusCode();

        var json =
            await response.Content.ReadAsStringAsync(
                cancellationToken);

        using var document =
            JsonDocument.Parse(json);

        var timeSeriesProperty =
            $"Time Series ({interval})";

        if (!document.RootElement.TryGetProperty(
                timeSeriesProperty,
                out var timeSeries))
        {
            ThrowAlphaVantageError(
                document.RootElement,
                $"Alpha Vantage did not return expected time series data for {symbol}.");
        }

        var candles = new List<StockCandle>();

        foreach (var item in timeSeries.EnumerateObject())
        {
            var timestamp = ParseEasternTimestamp(item.Name);

            var values = item.Value;

            var open = decimal.Parse(
                values.GetProperty("1. open").GetString()!,
                CultureInfo.InvariantCulture);

            var high = decimal.Parse(
                values.GetProperty("2. high").GetString()!,
                CultureInfo.InvariantCulture);

            var low = decimal.Parse(
                values.GetProperty("3. low").GetString()!,
                CultureInfo.InvariantCulture);

            var close = decimal.Parse(
                values.GetProperty("4. close").GetString()!,
                CultureInfo.InvariantCulture);

            var volume = long.Parse(
                values.GetProperty("5. volume").GetString()!,
                CultureInfo.InvariantCulture);

            candles.Add(new StockCandle
            {
                Id = Guid.NewGuid(),
                Symbol = symbol,
                Interval = candleInterval,
                Timestamp = timestamp,
                Open = open,
                High = high,
                Low = low,
                Close = close,
                Volume = volume
            });
        }

        return candles
            .OrderBy(x => x.Timestamp)
            .ToList();
    }

    // Alpha Vantage timestamps carry no offset. They must not be parsed in the
    // server's local time zone, or the same candle becomes a different instant
    // on every machine (and candle dedup, which is by timestamp, breaks).

    // Intraday times are US/Eastern (the response's "6. Time Zone").
    // America/New_York also handles the DST switch.
    private static readonly TimeZoneInfo MarketTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>"2026-09-01 16:00:00" (US/Eastern) as an absolute instant.</summary>
    private static DateTimeOffset ParseEasternTimestamp(string value)
    {
        var local = DateTime.ParseExact(
            value,
            "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture);

        return new DateTimeOffset(
            local,
            MarketTimeZone.GetUtcOffset(local));
    }

    /// <summary>
    /// "2026-09-01" (a trading day, not an instant) as UTC midnight of that date,
    /// so it is the same value whichever machine parses it.
    /// </summary>
    private static DateTimeOffset ParseTradingDate(string value)
    {
        var date = DateOnly.ParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture);

        return new DateTimeOffset(
            date.ToDateTime(TimeOnly.MinValue),
            TimeSpan.Zero);
    }

    // Alpha Vantage reports rate limits and other errors with a 200 status and an
    // "Information" or "Note" field in place of the expected data.
    [DoesNotReturn]
    private static void ThrowAlphaVantageError(
        JsonElement root,
        string fallbackMessage)
    {
        if (root.TryGetProperty(
                "Information",
                out var information))
        {
            throw new InvalidOperationException(
                $"Alpha Vantage error: {information.GetString()}");
        }

        if (root.TryGetProperty(
                "Note",
                out var note))
        {
            throw new InvalidOperationException(
                $"Alpha Vantage error: {note.GetString()}");
        }

        throw new InvalidOperationException(fallbackMessage);
    }
}