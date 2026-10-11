using System.Net;
using System.Text;
using System.Web;
using Microsoft.Extensions.Configuration;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure.MarketData.Providers;

namespace WaxyCandles.Tests.MarketData;

/// <summary>
/// Parses canned Alpha Vantage responses through a fake HttpMessageHandler.
/// No network access, no real API key.
/// </summary>
public class AlphaVantageMarketDataProviderTests
{
    private const string TestApiKey = "test-api-key";

    private const string RateLimitNote =
        "Thank you for using Alpha Vantage! Our standard API rate limit is 25 requests per day.";

    private const string QuoteJson = """
        {
          "Global Quote": {
            "01. symbol": "AAPL",
            "02. open": "149.0000",
            "03. high": "151.5000",
            "04. low": "148.2500",
            "05. price": "150.2500",
            "06. volume": "51234567",
            "07. latest trading day": "2026-09-01",
            "08. previous close": "149.0400",
            "09. change": "1.2100",
            "10. change percent": "0.8119%"
          }
        }
        """;

    private const string DailyJson = """
        {
          "Meta Data": { "2. Symbol": "AAPL" },
          "Time Series (Daily)": {
            "2026-09-02": { "1. open": "150.0", "2. high": "152.0", "3. low": "149.0", "4. close": "151.0", "5. volume": "2000" },
            "2026-09-01": { "1. open": "148.0", "2. high": "150.5", "3. low": "147.5", "4. close": "150.0", "5. volume": "1000" }
          }
        }
        """;

    private static string IntradayJson(string interval) => $$"""
        {
          "Meta Data": { "2. Symbol": "AAPL", "6. Time Zone": "US/Eastern" },
          "Time Series ({{interval}})": {
            "2026-09-01 16:00:00": { "1. open": "150.0", "2. high": "150.5", "3. low": "149.5", "4. close": "150.2", "5. volume": "300" },
            "2026-09-01 15:00:00": { "1. open": "149.0", "2. high": "150.0", "3. low": "148.5", "4. close": "150.0", "5. volume": "200" }
          }
        }
        """;

    private readonly FakeHandler _handler = new();

    [Fact]
    public async Task Quote_is_parsed_from_global_quote()
    {
        _handler.RespondWith(QuoteJson);

        var quote = await Provider().GetCurrentQuoteAsync(" aapl ");

        Assert.NotNull(quote);
        Assert.Equal("AAPL", quote.Symbol);
        Assert.Equal(150.25m, quote.Price);
        Assert.Equal(149.04m, quote.PreviousClose);
        Assert.Equal(1.21m, quote.Change);
        Assert.Equal(0.8119m, quote.ChangePercent);
        Assert.Equal(51_234_567, quote.Volume);
        Assert.Equal(new DateTime(2026, 9, 1), quote.LatestTradingDay!.Value.Date);
    }

    [Fact]
    public async Task Quote_request_uses_normalized_symbol_and_configured_key()
    {
        _handler.RespondWith(QuoteJson);

        await Provider().GetCurrentQuoteAsync(" brk.b ");

        var query = HttpUtility.ParseQueryString(_handler.Requests.Single().Query);
        Assert.Equal("GLOBAL_QUOTE", query["function"]);
        Assert.Equal("BRK.B", query["symbol"]);
        Assert.Equal(TestApiKey, query["apikey"]);
    }

    [Theory]
    [InlineData("Note")]
    [InlineData("Information")]
    public async Task Quote_rate_limit_response_throws_with_alpha_vantages_message(string key)
    {
        _handler.RespondWith($$"""{ "{{key}}": "{{RateLimitNote}}" }""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Provider().GetCurrentQuoteAsync("AAPL"));

        Assert.Equal($"Alpha Vantage error: {RateLimitNote}", ex.Message);
    }

    [Fact]
    public async Task Quote_response_without_any_known_key_throws()
    {
        _handler.RespondWith("{}");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Provider().GetCurrentQuoteAsync("AAPL"));

        Assert.Equal("Alpha Vantage did not return quote data for AAPL.", ex.Message);
    }

    [Fact]
    public async Task Quote_with_a_malformed_number_throws()
    {
        _handler.RespondWith(QuoteJson.Replace("\"150.2500\"", "\"N/A\""));

        await Assert.ThrowsAsync<FormatException>(
            () => Provider().GetCurrentQuoteAsync("AAPL"));
    }

    [Fact(Skip = KnownIssues.UnknownSymbolQuoteThrows)]
    public async Task Unknown_symbol_returns_no_quote()
    {
        // What Alpha Vantage actually returns for a symbol it doesn't know.
        _handler.RespondWith("""{ "Global Quote": {} }""");

        Assert.Null(await Provider().GetCurrentQuoteAsync("NOTASYMBOL"));
    }

    [Fact]
    public async Task Http_error_status_throws()
    {
        _handler.RespondWith("Service unavailable", HttpStatusCode.ServiceUnavailable);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => Provider().GetCurrentQuoteAsync("AAPL"));
    }

    [Fact]
    public async Task Missing_api_key_throws_before_calling_alpha_vantage()
    {
        var provider = Provider(apiKey: null);

        var quote = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetCurrentQuoteAsync("AAPL"));
        var candles = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetHistoricalCandlesAsync("AAPL", CandleInterval.OneDay));

        Assert.Equal("Alpha Vantage API key is not configured.", quote.Message);
        Assert.Equal("Alpha Vantage API key is not configured.", candles.Message);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Daily_candles_are_parsed()
    {
        _handler.RespondWith(DailyJson);

        var candles = await Provider().GetHistoricalCandlesAsync(" aapl ", CandleInterval.OneDay);

        Assert.Equal(2, candles.Count);
        Assert.All(candles, c =>
        {
            Assert.Equal("AAPL", c.Symbol);
            Assert.Equal(CandleInterval.OneDay, c.Interval);
            Assert.NotEqual(Guid.Empty, c.Id);
        });

        var day1 = candles.Single(c => c.Timestamp.Day == 1);
        Assert.Equal(148.0m, day1.Open);
        Assert.Equal(150.5m, day1.High);
        Assert.Equal(147.5m, day1.Low);
        Assert.Equal(150.0m, day1.Close);
        Assert.Equal(1000, day1.Volume);

        var query = HttpUtility.ParseQueryString(_handler.Requests.Single().Query);
        Assert.Equal("TIME_SERIES_DAILY", query["function"]);
        Assert.Equal("AAPL", query["symbol"]);
    }

    [Theory]
    [InlineData(CandleInterval.FiveMinutes, "5min")]
    [InlineData(CandleInterval.FifteenMinutes, "15min")]
    [InlineData(CandleInterval.ThirtyMinutes, "30min")]
    [InlineData(CandleInterval.OneHour, "60min")]
    public async Task Intraday_candles_are_parsed_and_returned_oldest_first(
        CandleInterval interval,
        string alphaVantageInterval)
    {
        _handler.RespondWith(IntradayJson(alphaVantageInterval));

        var candles = await Provider().GetHistoricalCandlesAsync("aapl", interval);

        Assert.Equal([15, 16], candles.Select(c => c.Timestamp.Hour));
        Assert.All(candles, c =>
        {
            Assert.Equal("AAPL", c.Symbol);
            Assert.Equal(interval, c.Interval);
        });
        Assert.Equal(150.2m, candles[1].Close);
        Assert.Equal(300, candles[1].Volume);

        var query = HttpUtility.ParseQueryString(_handler.Requests.Single().Query);
        Assert.Equal("TIME_SERIES_INTRADAY", query["function"]);
        Assert.Equal(alphaVantageInterval, query["interval"]);
    }

    [Fact]
    public async Task Intraday_rate_limit_response_throws_with_alpha_vantages_message()
    {
        _handler.RespondWith($$"""{ "Note": "{{RateLimitNote}}" }""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Provider().GetHistoricalCandlesAsync("AAPL", CandleInterval.OneHour));

        Assert.Equal($"Alpha Vantage error: {RateLimitNote}", ex.Message);
    }

    [Fact(Skip = KnownIssues.DailyCandlesSwallowProviderErrors)]
    public async Task Daily_rate_limit_response_throws_like_intraday_does()
    {
        _handler.RespondWith($$"""{ "Note": "{{RateLimitNote}}" }""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Provider().GetHistoricalCandlesAsync("AAPL", CandleInterval.OneDay));

        Assert.Equal($"Alpha Vantage error: {RateLimitNote}", ex.Message);
    }

    private AlphaVantageMarketDataProvider Provider(string? apiKey = TestApiKey) =>
        new(
            new HttpClient(_handler),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AlphaVantage:ApiKey"] = apiKey
                })
                .Build());

    private sealed class FakeHandler : HttpMessageHandler
    {
        private string _body = "{}";
        private HttpStatusCode _status = HttpStatusCode.OK;

        public List<Uri> Requests { get; } = [];

        public void RespondWith(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);

            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }
}
