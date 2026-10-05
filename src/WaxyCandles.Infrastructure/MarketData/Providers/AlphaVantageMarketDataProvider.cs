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
            if (document.RootElement.TryGetProperty(
                    "Information",
                    out var information))
            {
                throw new InvalidOperationException(
                    $"Alpha Vantage error: {information.GetString()}");
            }

            if (document.RootElement.TryGetProperty(
                    "Note",
                    out var note))
            {
                throw new InvalidOperationException(
                    $"Alpha Vantage error: {note.GetString()}");
            }

            throw new InvalidOperationException(
                $"Alpha Vantage did not return quote data for {symbol}.");
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
            DateTimeOffset.Parse(
                quote.GetProperty("07. latest trading day").GetString()!,
                CultureInfo.InvariantCulture);

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
            return [];
        }

        var candles = new List<StockCandle>();

        foreach (var day in timeSeries.EnumerateObject())
        {
            var date = DateTimeOffset.Parse(
                day.Name,
                CultureInfo.InvariantCulture);

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
            if (document.RootElement.TryGetProperty(
                    "Information",
                    out var information))
            {
                throw new InvalidOperationException(
                    $"Alpha Vantage error: {information.GetString()}");
            }

            if (document.RootElement.TryGetProperty(
                    "Note",
                    out var note))
            {
                throw new InvalidOperationException(
                    $"Alpha Vantage error: {note.GetString()}");
            }

            throw new InvalidOperationException(
                $"Alpha Vantage did not return expected time series data for {symbol}.");
        }

        var candles = new List<StockCandle>();

        foreach (var item in timeSeries.EnumerateObject())
        {
            var timestamp =
                DateTimeOffset.Parse(
                    item.Name,
                    CultureInfo.InvariantCulture);

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
}