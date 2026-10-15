using System.Net;
using System.Text;
using System.Web;
using Microsoft.Extensions.Configuration;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure.MarketData.Providers;

namespace WaxyCandles.Tests.MarketData;

/// <summary>
/// Parses canned Market Data (marketdata.app) responses through a fake
/// HttpMessageHandler. No network access, no real API token.
/// </summary>
public class MarketDataAppMarketDataProviderTests
{
    private const string TestApiToken = "test-api-token";

    private static long Unix(string utc) =>
        DateTimeOffset.Parse(utc).ToUnixTimeSeconds();

    // 16:00 EDT on 2026-09-01.
    private static readonly string QuoteJson = $$"""
        {
          "s": "ok",
          "symbol": ["AAPL"],
          "ask": [150.30], "askSize": [200],
          "bid": [150.20], "bidSize": [400],
          "mid": [150.25],
          "last": [150.25],
          "change": [1.21],
          "changepct": [0.008119],
          "volume": [51234567],
          "updated": [{{Unix("2026-09-01T20:00:00Z")}}]
        }
        """;

    // Daily candles are stamped midnight US/Eastern (04:00Z in summer).
    private static readonly string DailyJson = $$"""
        {
          "s": "ok",
          "t": [{{Unix("2026-09-01T04:00:00Z")}}, {{Unix("2026-09-02T04:00:00Z")}}],
          "o": [148.0, 150.0],
          "h": [150.5, 152.0],
          "l": [147.5, 149.0],
          "c": [150.0, 151.0],
          "v": [1000, 2000]
        }
        """;

    private readonly FakeHandler _handler = new();

    [Fact]
    public async Task Quote_is_parsed()
    {
        _handler.RespondWith(QuoteJson);

        var quote = await Provider().GetCurrentQuoteAsync(" aapl ");

        Assert.NotNull(quote);
        Assert.Equal("AAPL", quote.Symbol);
        Assert.Equal(150.25m, quote.Price);
        Assert.Equal(1.21m, quote.Change);
        Assert.Equal(149.04m, quote.PreviousClose);
        Assert.Equal(51_234_567, quote.Volume);
    }

    [Fact]
    public async Task Quote_change_percent_is_converted_from_a_fraction_to_percent()
    {
        _handler.RespondWith(QuoteJson);

        var quote = await Provider().GetCurrentQuoteAsync("AAPL");

        Assert.Equal(0.8119m, quote!.ChangePercent);
    }

    [Fact]
    public async Task Quote_request_uses_normalized_symbol_and_bearer_token()
    {
        _handler.RespondWith(QuoteJson);

        await Provider().GetCurrentQuoteAsync(" brk.b ");

        var request = _handler.Requests.Single();
        Assert.Equal("/v1/stocks/quotes/BRK.B/", request.Uri.AbsolutePath);
        Assert.Equal($"Bearer {TestApiToken}", request.Authorization);
        // The token goes in the header only, never the URL.
        Assert.DoesNotContain(TestApiToken, request.Uri.ToString());
    }

    [Theory]
    // 16:00 EDT: same date in Eastern and UTC.
    [InlineData("2026-09-01T20:00:00Z")]
    // 22:00 EDT on the 1st is already the 2nd in UTC.
    [InlineData("2026-09-02T02:00:00Z")]
    public async Task Quote_latest_trading_day_is_the_eastern_date_at_utc_midnight(string updatedUtc)
    {
        _handler.RespondWith(QuoteJson.Replace(
            Unix("2026-09-01T20:00:00Z").ToString(),
            Unix(updatedUtc).ToString()));

        var quote = await Provider().GetCurrentQuoteAsync("AAPL");

        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), quote!.LatestTradingDay);
        Assert.Equal(TimeSpan.Zero, quote.LatestTradingDay!.Value.Offset);
    }

    [Fact]
    public async Task Unknown_symbol_404_returns_no_quote()
    {
        _handler.RespondWith("""{ "s": "error", "errmsg": "No data." }""", HttpStatusCode.NotFound);

        Assert.Null(await Provider().GetCurrentQuoteAsync("NOTASYMBOL"));
    }

    [Fact]
    public async Task No_data_status_returns_no_quote()
    {
        _handler.RespondWith("""{ "s": "no_data" }""");

        Assert.Null(await Provider().GetCurrentQuoteAsync("NOTASYMBOL"));
    }

    [Fact]
    public async Task Cached_203_response_is_treated_as_success()
    {
        _handler.RespondWith(QuoteJson, HttpStatusCode.NonAuthoritativeInformation);

        Assert.NotNull(await Provider().GetCurrentQuoteAsync("AAPL"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.PaymentRequired)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Http_error_throws_with_market_datas_message(HttpStatusCode status)
    {
        _handler.RespondWith("""{ "s": "error", "errmsg": "Something went wrong." }""", status);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Provider().GetCurrentQuoteAsync("AAPL"));

        Assert.Equal($"Market Data error ({(int)status}): Something went wrong.", ex.Message);
    }

    [Fact]
    public async Task Non_json_gateway_error_throws()
    {
        _handler.RespondWith("<html>Bad gateway</html>", HttpStatusCode.BadGateway);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Provider().GetCurrentQuoteAsync("AAPL"));

        Assert.Equal("Market Data error (502): unknown error", ex.Message);
    }

    [Fact]
    public async Task Error_status_in_a_200_body_throws()
    {
        _handler.RespondWith("""{ "s": "error", "errmsg": "Invalid symbol." }""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Provider().GetCurrentQuoteAsync("AAPL"));

        Assert.Equal("Market Data error: Invalid symbol.", ex.Message);
    }

    [Fact]
    public async Task Missing_api_token_throws_before_calling_market_data()
    {
        var provider = Provider(apiToken: null);

        var quote = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetCurrentQuoteAsync("AAPL"));
        var candles = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetHistoricalCandlesAsync("AAPL", CandleInterval.OneDay));

        const string expected =
            "Market Data API token is not configured (MarketDataApp:ApiToken).";
        Assert.Equal(expected, quote.Message);
        Assert.Equal(expected, candles.Message);
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

        var day1 = candles[0];
        Assert.Equal(148.0m, day1.Open);
        Assert.Equal(150.5m, day1.High);
        Assert.Equal(147.5m, day1.Low);
        Assert.Equal(150.0m, day1.Close);
        Assert.Equal(1000, day1.Volume);
    }

    [Fact]
    public async Task Daily_candle_timestamp_is_utc_midnight_of_the_trading_date()
    {
        // Same convention as the Alpha Vantage provider, so candle dedup by
        // timestamp holds across a provider switch.
        _handler.RespondWith(DailyJson);

        var candles = await Provider().GetHistoricalCandlesAsync("AAPL", CandleInterval.OneDay);

        Assert.Equal(
            [
                new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero)
            ],
            candles.Select(c => c.Timestamp));
        Assert.All(candles, c => Assert.Equal(TimeSpan.Zero, c.Timestamp.Offset));
    }

    [Theory]
    [InlineData(CandleInterval.FiveMinutes, "5")]
    [InlineData(CandleInterval.FifteenMinutes, "15")]
    [InlineData(CandleInterval.ThirtyMinutes, "30")]
    [InlineData(CandleInterval.OneHour, "1H")]
    [InlineData(CandleInterval.OneDay, "D")]
    public async Task Candle_request_uses_the_matching_resolution_and_a_date_range(
        CandleInterval interval,
        string resolution)
    {
        _handler.RespondWith("""{ "s": "no_data" }""");

        await Provider().GetHistoricalCandlesAsync("aapl", interval);

        var request = _handler.Requests.Single();
        Assert.Equal($"/v1/stocks/candles/{resolution}/AAPL/", request.Uri.AbsolutePath);

        var query = HttpUtility.ParseQueryString(request.Uri.Query);
        var from = DateTimeOffset.FromUnixTimeSeconds(long.Parse(query["from"]!));
        var to = DateTimeOffset.FromUnixTimeSeconds(long.Parse(query["to"]!));
        Assert.True(from < to);
        Assert.True(to - from <= TimeSpan.FromDays(365), "Intraday is capped at 1 year per request.");
    }

    [Fact]
    public async Task Intraday_candles_are_absolute_instants_returned_oldest_first()
    {
        _handler.RespondWith($$"""
            {
              "s": "ok",
              "t": [{{Unix("2026-09-01T19:00:00Z")}}, {{Unix("2026-09-01T18:00:00Z")}}],
              "o": [150.0, 149.0], "h": [150.5, 150.0], "l": [149.5, 148.5],
              "c": [150.2, 150.0], "v": [300, 200]
            }
            """);

        var candles = await Provider().GetHistoricalCandlesAsync("AAPL", CandleInterval.OneHour);

        Assert.Equal(
            [
                DateTimeOffset.Parse("2026-09-01T18:00:00Z"),
                DateTimeOffset.Parse("2026-09-01T19:00:00Z")
            ],
            candles.Select(c => c.Timestamp));
        Assert.Equal(150.2m, candles[1].Close);
        Assert.Equal(300, candles[1].Volume);
    }

    [Fact]
    public async Task No_candles_returns_an_empty_list()
    {
        _handler.RespondWith("""{ "s": "no_data", "nextTime": 1788235200 }""");

        Assert.Empty(await Provider().GetHistoricalCandlesAsync("AAPL", CandleInterval.OneDay));
    }

    [Fact]
    public async Task Candles_404_returns_an_empty_list()
    {
        _handler.RespondWith("""{ "s": "error", "errmsg": "No data." }""", HttpStatusCode.NotFound);

        Assert.Empty(await Provider().GetHistoricalCandlesAsync("AAPL", CandleInterval.OneDay));
    }

    private MarketDataAppMarketDataProvider Provider(string? apiToken = TestApiToken) =>
        new(
            new HttpClient(_handler),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MarketDataApp:ApiToken"] = apiToken
                })
                .Build());

    private sealed record SentRequest(Uri Uri, string? Authorization);

    private sealed class FakeHandler : HttpMessageHandler
    {
        private string _body = "{}";
        private HttpStatusCode _status = HttpStatusCode.OK;

        public List<SentRequest> Requests { get; } = [];

        public void RespondWith(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new SentRequest(
                request.RequestUri!,
                request.Headers.Authorization?.ToString()));

            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }
}
