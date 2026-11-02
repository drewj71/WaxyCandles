using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WaxyCandles.Application.Options.Interfaces;
using WaxyCandles.Application.Options.Models;
using WaxyCandles.Tests.Infrastructure;
using WaxyCandles.Tests.Options;

namespace WaxyCandles.Tests.Api;

/// <summary>
/// The rate limits on the endpoints that spend vendor credits (per user) and on
/// login/register (per IP), with the limits lowered through configuration.
/// </summary>
public class RateLimitingApiTests(IntegrationFixture fixture) : ApiTestBase(fixture)
{
    private const int Limit = 3;

    private readonly FakeOptionsDataProvider _options = new();

    private WaxyCandlesApiFactory _limited = default!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        var settings = Settings();

        foreach (var policy in new[] { "MarketData", "HistoryImport", "Auth" })
        {
            settings[$"RateLimiting:{policy}:PermitLimit"] = Limit.ToString();
            // Long enough that the window can't roll over during a test.
            settings[$"RateLimiting:{policy}:Window"] = "00:10:00";
        }

        _limited = new WaxyCandlesApiFactory(settings, MarketData);
    }

    public override async Task DisposeAsync()
    {
        await _limited.DisposeAsync();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Gex_past_the_limit_is_429_per_user()
    {
        var factory = _limited.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IOptionsDataProvider>(_options)));

        MarketData.SetQuote("AAPL", 100m);
        _options.Chain = new OptionChain("AAPL", 100m, DateTimeOffset.UtcNow, []);

        var alice = NewClient(factory);
        (await RegisterAsync(alice.Http, "alice@example.com")).EnsureSuccessStatusCode();

        for (var i = 0; i < Limit; i++)
        {
            var allowed = await alice.Http.GetAsync("/api/stocks/AAPL/gex");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        var rejected = await alice.Http.GetAsync("/api/stocks/AAPL/gex");

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("Too many requests. Try again shortly.", await MessageAsync(rejected));
        Assert.True(rejected.Headers.Contains("Retry-After"));

        // Another user has their own allowance.
        var bob = NewClient(factory);
        (await RegisterAsync(bob.Http, "bob@example.com")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await bob.Http.GetAsync("/api/stocks/AAPL/gex")).StatusCode);
    }

    [Fact]
    public async Task Alert_create_and_update_share_the_market_data_limit()
    {
        var alice = NewClient(_limited);
        (await RegisterAsync(alice.Http, "alice@example.com")).EnsureSuccessStatusCode();

        var alert = new { symbol = "AAPL", targetPrice = 150m, direction = "Above" };

        var created = await alice.Http.PostAsJsonAsync("/api/alerts", alert, Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<IdOnly>(Json))!.Id;

        for (var i = 1; i < Limit; i++)
        {
            var updated = await alice.Http.PutAsJsonAsync($"/api/alerts/{id}", alert, Json);
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        }

        var rejected = await alice.Http.PostAsJsonAsync("/api/alerts", alert, Json);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);

        // Reads are not limited.
        Assert.Equal(HttpStatusCode.OK, (await alice.Http.GetAsync("/api/alerts")).StatusCode);
    }

    [Fact]
    public async Task History_import_past_the_limit_is_429()
    {
        var alice = NewClient(_limited);
        (await RegisterAsync(alice.Http, "alice@example.com")).EnsureSuccessStatusCode();

        for (var i = 0; i < Limit; i++)
        {
            var allowed = await alice.Http.PostAsync("/api/stocks/AAPL/historical?interval=OneDay", null);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        var rejected = await alice.Http.PostAsync("/api/stocks/AAPL/historical?interval=OneDay", null);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Fact]
    public async Task Login_and_register_past_the_limit_are_429_per_ip()
    {
        // TestServer requests have no remote IP, so every client here is the
        // same "IP": the limit holds across clients and accounts.
        (await RegisterAsync(NewClient(_limited).Http, "alice@example.com")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(NewClient(_limited).Http, "alice@example.com", "WrongPassword1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(NewClient(_limited).Http, "alice@example.com")).StatusCode);

        var login = await LoginAsync(NewClient(_limited).Http, "alice@example.com");
        var register = await RegisterAsync(NewClient(_limited).Http, "bob@example.com");

        Assert.Equal(HttpStatusCode.TooManyRequests, login.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, register.StatusCode);
        Assert.Equal("Too many requests. Try again shortly.", await MessageAsync(login));
    }

    [Fact]
    public async Task Refresh_and_logout_are_not_limited()
    {
        var alice = NewClient(_limited);
        (await RegisterAsync(alice.Http, "alice@example.com")).EnsureSuccessStatusCode();

        for (var i = 0; i < Limit + 2; i++)
        {
            (await alice.Http.PostAsync("/api/auth/refresh", null)).EnsureSuccessStatusCode();
        }

        (await alice.Http.PostAsync("/api/auth/logout", null)).EnsureSuccessStatusCode();
    }

    private static async Task<string?> MessageAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<MessageOnly>(Json))?.Message;

    private sealed record MessageOnly(string Message);

    private sealed record IdOnly(Guid Id);
}
