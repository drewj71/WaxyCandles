using System.Net;
using System.Text;
using System.Web;
using Microsoft.Extensions.Configuration;
using WaxyCandles.Application.Options.Models;
using WaxyCandles.Infrastructure.MarketData.Providers;

namespace WaxyCandles.Tests.Options;

/// <summary>
/// Market Data's options/chain through a fake HttpMessageHandler.
/// No network access, no real API token.
/// </summary>
public class MarketDataAppOptionsDataProviderTests
{
    private const string TestApiToken = "test-api-token";

    private static long Unix(string utc) =>
        DateTimeOffset.Parse(utc).ToUnixTimeSeconds();

    private static readonly OptionChainQuery Query = new(
        " aapl ",
        new DateOnly(2026, 10, 15),
        new DateOnly(2026, 11, 14),
        225m,
        275.5m);

    // Two calls and a put. The put has no Greeks (as on a historical chain) and
    // its snapshot is newer, so its underlying price is the chain's.
    private static readonly string ChainJson = $$"""
        {
          "s": "ok",
          "optionSymbol": ["AAPL261016C00250000", "AAPL261016C00255000", "AAPL261016P00245000"],
          "underlying": ["AAPL", "AAPL", "AAPL"],
          "expiration": [{{Unix("2026-10-16T20:00:00Z")}}, {{Unix("2026-10-16T20:00:00Z")}}, {{Unix("2026-10-16T20:00:00Z")}}],
          "side": ["call", "call", "put"],
          "strike": [250, 255, 245],
          "openInterest": [1200, null, 800],
          "mid": [3.15, 1.2, 2.05],
          "iv": [0.28, 0.27, null],
          "gamma": [0.041, 0.033, null],
          "underlyingPrice": [249.5, 249.5, 249.8],
          "updated": [{{Unix("2026-10-15T19:58:00Z")}}, {{Unix("2026-10-15T19:58:00Z")}}, {{Unix("2026-10-15T19:59:00Z")}}]
        }
        """;

    private readonly FakeHandler _handler = new();

    [Fact]
    public async Task Request_filters_by_expiration_and_strike_range_with_the_bearer_token()
    {
        _handler.RespondWith(ChainJson);

        await Provider().GetOptionChainAsync(Query);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("/v1/options/chain/AAPL/", request.Uri.AbsolutePath);

        var query = HttpUtility.ParseQueryString(request.Uri.Query);
        Assert.Equal("2026-10-15", query["from"]);
        Assert.Equal("2026-11-14", query["to"]);
        Assert.Equal("225-275.5", query["strike"]);
        // Blank mode: the account's default, the only choice on a trial.
        Assert.Null(query["mode"]);
        Assert.Equal($"Bearer {TestApiToken}", request.Authorization);
        Assert.DoesNotContain(TestApiToken, request.Uri.ToString());
    }

    [Theory]
    [InlineData("cached")]
    [InlineData("live")]
    public async Task Configured_mode_is_sent(string mode)
    {
        _handler.RespondWith(ChainJson);

        await Provider(mode).GetOptionChainAsync(Query);

        Assert.Equal(mode, HttpUtility.ParseQueryString(_handler.Requests.Single().Uri.Query)["mode"]);
    }

    [Fact]
    public async Task Unknown_mode_throws_without_a_request()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Provider("historical").GetOptionChainAsync(Query));

        Assert.Contains("MarketDataApp:OptionsMode", ex.Message);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Contracts_are_parsed_with_missing_values_as_null_or_zero()
    {
        _handler.RespondWith(ChainJson);

        var chain = await Provider().GetOptionChainAsync(Query);

        Assert.NotNull(chain);
        Assert.Equal("AAPL", chain.Underlying);
        Assert.Equal(3, chain.Contracts.Count);

        var call = chain.Contracts[0];
        Assert.Equal(OptionSide.Call, call.Side);
        Assert.Equal(250m, call.Strike);
        Assert.Equal(DateTimeOffset.Parse("2026-10-16T20:00:00Z"), call.Expiration);
        Assert.Equal(1200, call.OpenInterest);
        Assert.Equal(0.041, call.Gamma);
        Assert.Equal(0.28, call.ImpliedVolatility);
        Assert.Equal(3.15m, call.Mid);

        Assert.Equal(0, chain.Contracts[1].OpenInterest);

        var put = chain.Contracts[2];
        Assert.Equal(OptionSide.Put, put.Side);
        Assert.Null(put.Gamma);
        Assert.Null(put.ImpliedVolatility);
    }

    [Fact]
    public async Task Underlying_price_and_as_of_come_from_the_newest_snapshot()
    {
        _handler.RespondWith(ChainJson);

        var chain = await Provider().GetOptionChainAsync(Query);

        Assert.Equal(249.8m, chain!.UnderlyingPrice);
        Assert.Equal(DateTimeOffset.Parse("2026-10-15T19:59:00Z"), chain.AsOf);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{ "s": "error", "errmsg": "No data." }""")]
    [InlineData(HttpStatusCode.OK, """{ "s": "no_data" }""")]
    // Cached mode with nothing in the cache.
    [InlineData(HttpStatusCode.NoContent, "")]
    public async Task No_data_returns_null(HttpStatusCode status, string body)
    {
        _handler.RespondWith(body, status);

        Assert.Null(await Provider().GetOptionChainAsync(Query));
    }

    [Fact]
    public async Task Payment_required_throws_with_market_datas_message()
    {
        _handler.RespondWith("""{ "s": "error", "errmsg": "Upgrade your plan." }""", HttpStatusCode.PaymentRequired);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Provider("cached").GetOptionChainAsync(Query));

        Assert.Equal("Market Data error (402): Upgrade your plan.", ex.Message);
    }

    private MarketDataAppOptionsDataProvider Provider(string? mode = null) =>
        new(
            new HttpClient(_handler),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MarketDataApp:ApiToken"] = TestApiToken,
                    ["MarketDataApp:OptionsMode"] = mode
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
