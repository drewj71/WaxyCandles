using System.Net;
using System.Text;
using System.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WaxyCandles.Infrastructure.MarketData.Calendars;

namespace WaxyCandles.Tests.MarketData;

/// <summary>
/// Market Data's markets/status through a fake HttpMessageHandler and clock.
/// No network access, no real API token.
/// </summary>
public class MarketDataAppMarketCalendarTests
{
    private const string TestApiToken = "test-api-token";

    // Labor Day week 2026: Monday the 7th is a holiday.
    private static readonly DateOnly LaborDay = new(2026, 9, 7);
    private static readonly DateOnly Tuesday = new(2026, 9, 8);
    private static readonly DateOnly Wednesday = new(2026, 9, 9);

    private readonly FakeHandler _handler = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-09-04T12:00:00Z"));

    // Dates are midnight US/Eastern as Unix seconds, as the API sends them.
    private static long Midnight(DateOnly date) =>
        DateTimeOffset.Parse($"{date:yyyy-MM-dd}T04:00:00Z").ToUnixTimeSeconds();

    private static string StatusJson(params (DateOnly Date, string? Status)[] days) =>
        $$"""
        {
          "s": "ok",
          "date": [{{string.Join(", ", days.Select(d => Midnight(d.Date)))}}],
          "status": [{{string.Join(", ", days.Select(d => d.Status is null ? "null" : $"\"{d.Status}\""))}}]
        }
        """;

    [Fact]
    public async Task Open_and_closed_days_are_read_from_the_status()
    {
        _handler.RespondWith(StatusJson((LaborDay, "closed"), (Tuesday, "open")));
        var calendar = Calendar();

        Assert.False(await calendar.IsTradingDayAsync(LaborDay));
        Assert.True(await calendar.IsTradingDayAsync(Tuesday));
    }

    [Fact]
    public async Task One_request_covers_the_next_30_days()
    {
        _handler.RespondWith(StatusJson((LaborDay, "closed"), (Tuesday, "open"), (Wednesday, "open")));
        var calendar = Calendar();

        await calendar.IsTradingDayAsync(LaborDay);
        await calendar.IsTradingDayAsync(Tuesday);
        await calendar.IsTradingDayAsync(Wednesday);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("/v1/markets/status/", request.Uri.AbsolutePath);
        var query = HttpUtility.ParseQueryString(request.Uri.Query);
        Assert.Equal("2026-09-07", query["from"]);
        Assert.Equal("2026-10-06", query["to"]);
        Assert.Equal($"Bearer {TestApiToken}", request.Authorization);
    }

    [Fact]
    public async Task Weekends_are_closed_without_asking()
    {
        var calendar = Calendar();

        Assert.False(await calendar.IsTradingDayAsync(new DateOnly(2026, 9, 5)));
        Assert.False(await calendar.IsTradingDayAsync(new DateOnly(2026, 9, 6)));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Date_the_api_has_no_answer_for_counts_as_trading_and_is_not_asked_again()
    {
        _handler.RespondWith(StatusJson((Tuesday, null)));
        var calendar = Calendar();

        Assert.True(await calendar.IsTradingDayAsync(Tuesday));
        Assert.True(await calendar.IsTradingDayAsync(Tuesday));
        // Wednesday was in the requested range but missing from the response.
        Assert.True(await calendar.IsTradingDayAsync(Wednesday));

        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task No_data_response_counts_as_trading()
    {
        _handler.RespondWith("""{ "s": "no_data" }""");

        Assert.True(await Calendar().IsTradingDayAsync(Tuesday));
    }

    [Theory]
    [InlineData(HttpStatusCode.PaymentRequired)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Failure_counts_as_trading_and_is_retried_after_15_minutes(HttpStatusCode status)
    {
        _handler.RespondWith("""{ "s": "error", "errmsg": "Nope." }""", status);
        var calendar = Calendar();

        // Treating a holiday as a trading day only wastes credits; the reverse
        // would silence every alert for the day.
        Assert.True(await calendar.IsTradingDayAsync(LaborDay));

        _time.Advance(TimeSpan.FromMinutes(14));
        Assert.True(await calendar.IsTradingDayAsync(LaborDay));
        Assert.Single(_handler.Requests);

        _handler.RespondWith(StatusJson((LaborDay, "closed")));
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.False(await calendar.IsTradingDayAsync(LaborDay));
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task Timeout_counts_as_trading()
    {
        _handler.ThrowOnSend = new TaskCanceledException("HttpClient timeout");

        Assert.True(await Calendar().IsTradingDayAsync(LaborDay));
    }

    [Fact]
    public async Task Missing_token_counts_as_trading_without_a_request()
    {
        Assert.True(await Calendar(apiToken: null).IsTradingDayAsync(LaborDay));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Cache_is_reloaded_after_a_day()
    {
        _handler.RespondWith(StatusJson((Tuesday, null)));
        var calendar = Calendar();
        await calendar.IsTradingDayAsync(Tuesday);

        _handler.RespondWith(StatusJson((Tuesday, "closed")));
        _time.Advance(TimeSpan.FromDays(1));

        Assert.False(await calendar.IsTradingDayAsync(Tuesday));
        Assert.Equal(2, _handler.Requests.Count);
    }

    private MarketDataAppMarketCalendar Calendar(string? apiToken = TestApiToken) =>
        new(
            new FakeHttpClientFactory(_handler),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MarketDataApp:ApiToken"] = apiToken
                })
                .Build(),
            _time,
            NullLogger<MarketDataAppMarketCalendar>.Instance);

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed record SentRequest(Uri Uri, string? Authorization);

    private sealed class FakeHandler : HttpMessageHandler
    {
        private string _body = "{}";
        private HttpStatusCode _status = HttpStatusCode.OK;

        public List<SentRequest> Requests { get; } = [];

        public Exception? ThrowOnSend { get; set; }

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

            if (ThrowOnSend is not null)
            {
                throw ThrowOnSend;
            }

            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }
}
