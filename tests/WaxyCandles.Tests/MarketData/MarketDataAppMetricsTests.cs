using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using WaxyCandles.Application.Options.Models;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure.MarketData.Providers;
using WaxyCandles.Infrastructure.Telemetry;
using WaxyCandles.Tests.Telemetry;
using static WaxyCandles.Infrastructure.Telemetry.WaxyCandlesMetrics;

namespace WaxyCandles.Tests.MarketData;

/// <summary>
/// Market Data request and credit metrics, through a fake HttpMessageHandler.
/// Credits come from the X-Api-Ratelimit-Consumed header Market Data sends on
/// every response. Not run in parallel: other tests use the same client.
/// </summary>
[Collection(MetricsCollection.Name)]
public sealed class MarketDataAppMetricsTests : IDisposable
{
    private const string Requests = "waxycandles.marketdata.requests";
    private const string Credits = "waxycandles.marketdata.credits";

    private static readonly string PricesJson = $$"""
        {
          "s": "ok",
          "symbol": ["AAPL", "MSFT"],
          "mid": [150.25, 410.5],
          "change": [1.21, -2.0],
          "changepct": [0.008, -0.0048],
          "updated": [{{Now}}, {{Now}}]
        }
        """;

    private static readonly string ChainJson = $$"""
        {
          "s": "ok",
          "optionSymbol": ["AAPL261016C00250000", "AAPL261016P00245000"],
          "side": ["call", "put"],
          "strike": [250, 245],
          "expiration": [{{Now}}, {{Now}}],
          "openInterest": [1200, 800],
          "mid": [3.15, 2.05],
          "iv": [0.28, 0.3],
          "gamma": [0.041, 0.03],
          "underlyingPrice": [249.5, 249.5],
          "updated": [{{Now}}, {{Now}}]
        }
        """;

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private readonly FakeHandler _handler = new();
    private readonly MetricRecorder _metrics = new();

    public void Dispose() => _metrics.Dispose();

    [Fact]
    public async Task Quote_request_records_its_credits_from_the_response_header_and_the_balance_left()
    {
        _handler.RespondWith(PricesJson, consumed: 2, remaining: 97);

        var quotes = await QuoteProvider().GetCurrentQuotesAsync(["AAPL", "MSFT"]);

        Assert.Equal(2, quotes.Count);
        Assert.Equal(1, _metrics.Sum(Requests, (EndpointTag, "prices"), (OutcomeTag, "ok")));
        Assert.Equal(2, _metrics.Sum(Credits, (EndpointTag, "prices")));
        Assert.Equal(97, _metrics.Gauge("waxycandles.marketdata.credits_remaining"));
        Assert.Equal(97, WaxyCandlesMetrics.MarketDataCreditsRemaining);
    }

    [Fact]
    public async Task Option_chain_records_the_credits_billed_per_contract()
    {
        // Live/delayed chains cost 1 credit per contract returned.
        _handler.RespondWith(ChainJson, consumed: 2, remaining: 50);

        var chain = await new MarketDataAppOptionsDataProvider(Client(), Configuration())
            .GetOptionChainAsync(new OptionChainQuery(
                "AAPL",
                DateOnly.FromDateTime(DateTime.UtcNow),
                DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
                200m,
                300m));

        Assert.NotNull(chain);
        Assert.Equal(1, _metrics.Sum(Requests, (EndpointTag, "option_chain"), (OutcomeTag, "ok")));
        Assert.Equal(2, _metrics.Sum(Credits, (EndpointTag, "option_chain")));
    }

    [Fact]
    public async Task No_data_and_errors_are_counted_by_outcome_and_unbilled_responses_add_no_credits()
    {
        _handler.RespondWith("""{ "s": "no_data" }""", HttpStatusCode.NotFound, consumed: 0);
        Assert.Empty(await QuoteProvider().GetHistoricalCandlesAsync("AAPL", CandleInterval.OneDay));

        _handler.RespondWith("""{ "s": "error", "errmsg": "Bad token" }""", HttpStatusCode.Unauthorized);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => QuoteProvider().GetCurrentQuotesAsync(["AAPL"]));

        Assert.Equal(1, _metrics.Sum(Requests, (EndpointTag, "candles"), (OutcomeTag, "no_data")));
        Assert.Equal(1, _metrics.Sum(Requests, (EndpointTag, "prices"), (OutcomeTag, "error")));
        Assert.Empty(_metrics.Measurements(Credits));
    }

    [Fact]
    public async Task Transport_failure_is_counted_as_an_error_and_still_thrown()
    {
        _handler.Throw = new HttpRequestException("Connection refused");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => QuoteProvider().GetCurrentQuotesAsync(["AAPL"]));

        Assert.Equal(1, _metrics.Sum(Requests, (EndpointTag, "prices"), (OutcomeTag, "error")));
    }

    [Fact]
    public async Task Tags_never_include_the_symbol()
    {
        _handler.RespondWith(PricesJson, consumed: 2, remaining: 10);

        await QuoteProvider().GetCurrentQuotesAsync(["AAPL", "MSFT"]);

        var all = _metrics.Measurements(Requests).Concat(_metrics.Measurements(Credits)).ToList();

        Assert.NotEmpty(all);
        Assert.All(all, m => Assert.All(
            m.Tags.Values,
            v => Assert.DoesNotContain("AAPL", v?.ToString() ?? "")));
        Assert.All(all, m => Assert.Subset(
            new HashSet<string> { EndpointTag, OutcomeTag },
            m.Tags.Keys.ToHashSet()));
    }

    private MarketDataAppMarketDataProvider QuoteProvider() => new(Client(), Configuration());

    private HttpClient Client() => new(_handler);

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketDataApp:ApiToken"] = "test-api-token"
            })
            .Build();

    private sealed class FakeHandler : HttpMessageHandler
    {
        private string _body = "{}";
        private HttpStatusCode _status = HttpStatusCode.OK;
        private long? _consumed;
        private long? _remaining;

        public Exception? Throw { get; set; }

        public void RespondWith(
            string body,
            HttpStatusCode status = HttpStatusCode.OK,
            long? consumed = null,
            long? remaining = null)
        {
            _body = body;
            _status = status;
            _consumed = consumed;
            _remaining = remaining;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };

            if (_consumed is not null)
            {
                response.Headers.Add("X-Api-Ratelimit-Consumed", _consumed.Value.ToString());
            }

            if (_remaining is not null)
            {
                response.Headers.Add("X-Api-Ratelimit-Remaining", _remaining.Value.ToString());
            }

            return Task.FromResult(response);
        }
    }
}
