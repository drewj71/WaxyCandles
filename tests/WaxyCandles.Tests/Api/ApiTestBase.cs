using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WaxyCandles.Api.Controllers;
using WaxyCandles.Application.MarketData.Interfaces;
using WaxyCandles.Tests.Infrastructure;
using WaxyCandles.Tests.MarketData;

namespace WaxyCandles.Tests.Api;

/// <summary>
/// Hosts the real API (Program.cs, middleware, auth, controllers) in memory against
/// the test database. Only the external market data provider is faked.
/// </summary>
public sealed class WaxyCandlesApiFactory(
    IReadOnlyDictionary<string, string?> settings,
    FakeMarketDataProvider marketData)
    : WebApplicationFactory<AlertsController>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not "Development", so the developer's real user secrets are never loaded.
        builder.UseEnvironment("Testing");

        // UseSetting makes the values visible while Program.cs registers services
        // (AddJwtBearer reads Jwt:* immediately); the in-memory source makes sure
        // they win over the blank placeholders in appsettings.json.
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(settings));

        // Tests deliberately trigger errors; keep the API's console logging out of test output.
        builder.ConfigureLogging(logging => logging.ClearProviders());

        builder.ConfigureTestServices(services =>
            services.AddSingleton<IMarketDataProvider>(marketData));
    }
}

/// <summary>A browser-like client: keeps cookies between requests, like the React app.</summary>
public sealed record ApiClient(HttpClient Http, CookieContainer Cookies)
{
    public string? Cookie(string name) =>
        Cookies.GetCookies(ApiTestBase.BaseAddress)[name]?.Value;
}

public abstract class ApiTestBase(IntegrationFixture fixture)
    : DatabaseTestBase(fixture)
{
    public const string AccessTokenCookie = "waxy_access_token";
    public const string RefreshTokenCookie = "waxy_refresh_token";
    public const string Password = "Password123";

    // https, because the auth cookies are Secure and would not be sent back over http.
    public static readonly Uri BaseAddress = new("https://localhost");

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    protected FakeMarketDataProvider MarketData { get; } = new();

    protected WaxyCandlesApiFactory Factory { get; private set; } = default!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        Factory = new WaxyCandlesApiFactory(Fixture.TestSettings(ConnectionString), MarketData);
    }

    public override async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await base.DisposeAsync();
    }

    protected ApiClient NewClient()
    {
        var cookies = new CookieContainer();
        var http = Factory.CreateDefaultClient(BaseAddress, new CookieContainerHandler(cookies));

        return new ApiClient(http, cookies);
    }

    /// <summary>A client with no cookie handling, for sending hand-crafted cookies.</summary>
    protected HttpClient NewRawClient() => Factory.CreateDefaultClient(BaseAddress);

    protected async Task<ApiClient> RegisteredClientAsync(string email)
    {
        var client = NewClient();
        var response = await RegisterAsync(client.Http, email);
        response.EnsureSuccessStatusCode();
        return client;
    }

    protected static Task<HttpResponseMessage> RegisterAsync(
        HttpClient http,
        string email,
        string password = Password,
        string? confirmPassword = null) =>
        http.PostAsJsonAsync(
            "/api/auth/register",
            new { email, password, confirmPassword = confirmPassword ?? password },
            Json);

    protected static Task<HttpResponseMessage> LoginAsync(
        HttpClient http,
        string email,
        string password = Password) =>
        http.PostAsJsonAsync("/api/auth/login", new { email, password }, Json);

    protected static HttpRequestMessage WithCookie(
        HttpMethod method,
        string url,
        string name,
        string value)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", $"{name}={value}");
        return request;
    }
}
