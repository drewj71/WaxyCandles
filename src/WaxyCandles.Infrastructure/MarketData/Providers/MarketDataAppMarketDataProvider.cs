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
    private const string BaseUrl = MarketDataAppApi.BaseUrl;

    // How far back an import reaches. Intraday is capped at 1 year per request
    // by the API; candles cost 1 credit per 1,000, so these are cheap.
    private static readonly TimeSpan IntradayLookback = TimeSpan.FromDays(30);
    private static readonly TimeSpan DailyLookback = TimeSpan.FromDays(365);

    private const int QuoteBatchSize = 100;

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
        var quotes = await GetCurrentQuotesAsync(
            [symbol],
            cancellationToken);

        return quotes.SingleOrDefault();
    }

    public async Task<IReadOnlyList<MarketQuote>> GetCurrentQuotesAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken cancellationToken = default)
    {
        var quotes = new List<MarketQuote>();

        var normalizedSymbols = symbols
            .Select(x => x.Trim().ToUpperInvariant())
            .Distinct();

        // One request per batch costs the same credits (1 per symbol) as one per
        // symbol. Batches keep the URL well under the API's 4,094-byte limit.
        foreach (var batch in normalizedSymbols.Chunk(QuoteBatchSize))
        {
            quotes.AddRange(await GetQuoteBatchAsync(
                batch,
                cancellationToken));
        }

        return quotes;
    }

    private async Task<IReadOnlyList<MarketQuote>> GetQuoteBatchAsync(
        string[] symbols,
        CancellationToken cancellationToken)
    {
        // stocks/prices, not stocks/quotes: quotes are delayed or a day old unless
        // the account has the exchange agreements, while prices are real-time on
        // every plan. The price is a SmartMid (bid/ask midpoint), not the last
        // trade, and the response has no volume.
        using var document = await GetAsync(
            $"{BaseUrl}/stocks/prices/" +
            $"?symbols={string.Join(',', symbols.Select(Uri.EscapeDataString))}",
            cancellationToken);

        // No data for any symbol: HTTP 404 or s = "no_data".
        if (document is null)
        {
            return [];
        }

        var root = document.RootElement;

        var returnedSymbols = root.GetProperty("symbol");
        var mids = root.GetProperty("mid");
        var changes = root.GetProperty("change");
        var changePercents = root.GetProperty("changepct");
        var updated = root.GetProperty("updated");

        var quotes = new List<MarketQuote>();

        for (var i = 0; i < returnedSymbols.GetArrayLength(); i++)
        {
            // A symbol without data in an otherwise successful batch.
            if (mids[i].ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            var price = mids[i].GetDecimal();
            var change = changes[i].GetDecimal();
            var quotedAt = DateTimeOffset.FromUnixTimeSeconds(updated[i].GetInt64());

            quotes.Add(new MarketQuote
            {
                Symbol = returnedSymbols[i].GetString()!.ToUpperInvariant(),
                Price = price,
                PreviousClose = price - change,
                Change = change,
                // Market Data returns a fraction (0.0328); the app stores percent (3.28).
                ChangePercent = changePercents[i].GetDecimal() * 100m,
                LatestTradingDay = ToTradingDate(updated[i].GetInt64()),
                QuotedAt = quotedAt
            });
        }

        return quotes;
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

    private Task<JsonDocument?> GetAsync(
        string url,
        CancellationToken cancellationToken) =>
        MarketDataAppApi.GetAsync(
            _httpClient,
            _configuration,
            url,
            cancellationToken);

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
