using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WaxyCandles.Api.Controllers;
using WaxyCandles.Api.Middleware;
using WaxyCandles.Application.MarketData.Exceptions;
using WaxyCandles.Application.Options.Interfaces;
using WaxyCandles.Infrastructure.MarketData.Providers;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Api;

/// <summary>
/// A vendor timeout or rate limit, through the real API: 504 / 503 from
/// ExceptionHandlingMiddleware, distinct from the API's own 429. The option
/// chain goes through the real Market Data provider over a fake handler, so
/// the classification at the HTTP boundary is covered end to end.
/// </summary>
public class MarketDataUnavailableApiTests(IntegrationFixture fixture) : ApiTestBase(fixture)
{
    private const string VendorSecretMarker = "vendor-body-do-not-leak";

    private readonly VendorHandler _vendor = new();
    private readonly StatusRecorder _statuses = new();

    [Fact]
    public async Task Gex_vendor_timeout_is_504_with_a_clear_message()
    {
        _vendor.Stall = true;
        var http = await SignedInAsync(VendorFactory(TimeSpan.FromMilliseconds(200)));

        var response = await http.GetAsync("/api/stocks/AAPL/gex");

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal(ExceptionHandlingMiddleware.MarketDataTimeoutMessage, await MessageAsync(response));
        Assert.False(response.Headers.Contains("Retry-After"));
        // Never retried: the vendor saw exactly one request.
        Assert.Equal(1, _vendor.Calls);
    }

    [Fact]
    public async Task Gex_vendor_429_is_503_with_the_vendors_retry_after()
    {
        _vendor.Respond = HttpStatusCode.TooManyRequests;
        _vendor.RetryAfter = TimeSpan.FromSeconds(120);
        var http = await SignedInAsync(VendorFactory());

        var response = await http.GetAsync("/api/stocks/AAPL/gex");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(ExceptionHandlingMiddleware.MarketDataRateLimitedMessage, await MessageAsync(response));
        Assert.Equal(TimeSpan.FromSeconds(120), response.Headers.RetryAfter?.Delta);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(VendorSecretMarker, body);
        Assert.DoesNotContain("marketdata.app", body);
        Assert.Equal(1, _vendor.Calls);
    }

    [Fact]
    public async Task Gex_client_abort_is_not_a_504()
    {
        _vendor.Stall = true;
        // A vendor timeout far longer than the test, so only the abort can end the call.
        var http = await SignedInAsync(VendorFactory(TimeSpan.FromMinutes(5)));

        using var cts = new CancellationTokenSource();
        var request = http.GetAsync("/api/stocks/AAPL/gex", cts.Token);

        await _vendor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        var status = await _statuses.Next("/api/stocks/AAPL/gex").WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotEqual(StatusCodes.Status504GatewayTimeout, status);
        Assert.Equal(ExceptionHandlingMiddleware.ClientClosedRequest, status);
    }

    [Fact]
    public async Task Level_alert_create_with_the_vendor_rate_limited_is_503()
    {
        _vendor.Respond = HttpStatusCode.TooManyRequests;
        var http = await SignedInAsync(VendorFactory());

        var response = await http.PostAsJsonAsync("/api/alerts", new
        {
            symbol = "AAPL",
            level = "King",
            levelProximityPercent = 0.5,
            levelDays = 30,
            levelStrikeRange = 10,
            frequency = "EveryCross"
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(ExceptionHandlingMiddleware.MarketDataRateLimitedMessage, await MessageAsync(response));
        // No vendor hint, so no Retry-After.
        Assert.False(response.Headers.Contains("Retry-After"));
    }

    [Theory]
    [InlineData(MarketDataUnavailableReason.Timeout, HttpStatusCode.GatewayTimeout)]
    [InlineData(MarketDataUnavailableReason.RateLimited, HttpStatusCode.ServiceUnavailable)]
    public async Task History_import_maps_vendor_failures(
        MarketDataUnavailableReason reason,
        HttpStatusCode expected)
    {
        MarketData.Throw = new MarketDataUnavailableException(reason, "Market Data");
        var http = await SignedInAsync(Factory);

        var response = await http.PostAsync("/api/stocks/AAPL/historical?interval=OneDay", null);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task The_apis_own_rate_limit_is_still_a_429_not_a_503()
    {
        var settings = Settings();
        settings["RateLimiting:MarketData:PermitLimit"] = "1";
        settings["RateLimiting:MarketData:Window"] = "00:10:00";
        _vendor.Respond = HttpStatusCode.TooManyRequests;

        await using var limited = new WaxyCandlesApiFactory(settings, MarketData);
        var http = await SignedInAsync(WithVendor(limited, TimeSpan.FromSeconds(20)));

        // The first spends the API's allowance and reaches the (rate-limited) vendor...
        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            (await http.GetAsync("/api/stocks/AAPL/gex")).StatusCode);

        // ...the second is refused by the API itself, before any vendor call.
        var rejected = await http.GetAsync("/api/stocks/AAPL/gex");

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("Too many requests. Try again shortly.", await MessageAsync(rejected));
        Assert.True(rejected.Headers.Contains("Retry-After"));
        Assert.Equal(1, _vendor.Calls);
    }

    private WebApplicationFactory<AlertsController> VendorFactory(TimeSpan? timeout = null) =>
        WithVendor(Factory, timeout ?? TimeSpan.FromSeconds(20));

    /// <summary>
    /// The real Market Data options provider over <see cref="_vendor"/>, with
    /// the given HttpClient timeout, and the status of every response recorded.
    /// </summary>
    private WebApplicationFactory<AlertsController> WithVendor(
        WebApplicationFactory<AlertsController> factory,
        TimeSpan timeout)
    {
        MarketData.SetQuote("AAPL", 100m);

        return factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter>(_statuses);
                services.AddSingleton<IOptionsDataProvider>(_ =>
                    new MarketDataAppOptionsDataProvider(
                        new HttpClient(_vendor, disposeHandler: false) { Timeout = timeout },
                        new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                // For the fake handler only; nothing is sent anywhere.
                                ["MarketDataApp:ApiToken"] = "test-token-not-a-secret"
                            })
                            .Build()));
            }));
    }

    private static async Task<HttpClient> SignedInAsync(WebApplicationFactory<AlertsController> factory)
    {
        var client = NewClient(factory);
        (await RegisterAsync(client.Http, $"{Guid.NewGuid():N}@example.com")).EnsureSuccessStatusCode();
        return client.Http;
    }

    private static async Task<string?> MessageAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<MessageOnly>(Json))?.Message;

    private sealed record MessageOnly(string Message);

    /// <summary>Stalls until cancelled, or answers with a fixed status.</summary>
    private sealed class VendorHandler : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public bool Stall { get; set; }

        public HttpStatusCode Respond { get; set; } = HttpStatusCode.OK;

        public TimeSpan? RetryAfter { get; set; }

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();

            if (Stall)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            var response = new HttpResponseMessage(Respond)
            {
                Content = new StringContent(
                    $$"""{ "s": "error", "errmsg": "{{VendorSecretMarker}}" }""")
            };

            if (RetryAfter is { } delay)
            {
                response.Headers.RetryAfter = new(delay);
            }

            return response;
        }
    }

    /// <summary>
    /// Outermost middleware: records the status each request ends with,
    /// including one whose client has gone (which the client never sees).
    /// </summary>
    private sealed class StatusRecorder : IStartupFilter
    {
        private readonly Dictionary<string, TaskCompletionSource<int>> _byPath = [];

        public Task<int> Next(string path)
        {
            lock (_byPath)
            {
                return Get(path).Task;
            }
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, inner) =>
                {
                    try
                    {
                        await inner(context);
                    }
                    finally
                    {
                        lock (_byPath)
                        {
                            Get(context.Request.Path.Value ?? "").TrySetResult(context.Response.StatusCode);
                        }
                    }
                });

                next(app);
            };

        private TaskCompletionSource<int> Get(string path)
        {
            if (!_byPath.TryGetValue(path, out var source))
            {
                source = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _byPath[path] = source;
            }

            return source;
        }
    }
}
