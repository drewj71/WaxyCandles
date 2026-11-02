using System.Net;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Api;

/// <summary>
/// Cookie auth only works cross-origin if CORS allows credentials for exactly the
/// configured origins (Cors:AllowedOrigins; the tests configure the Vite dev
/// origin), and no other.
/// </summary>
public class CorsTests(IntegrationFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task Frontend_origin_is_allowed_with_credentials()
    {
        var response = await PreflightAsync("http://localhost:5173");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(
            "http://localhost:5173",
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal(
            "true",
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials")));
    }

    [Fact]
    public async Task Other_origins_are_not_allowed()
    {
        var response = await PreflightAsync("https://evil.example.com");

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Allowed_origins_come_from_configuration()
    {
        var settings = Settings();
        settings["Cors:AllowedOrigins:0"] = "https://app.example.com";
        settings["Cors:AllowedOrigins:1"] = "https://staging.example.com";
        await using var factory = new WaxyCandlesApiFactory(settings, MarketData);
        var http = factory.CreateDefaultClient(BaseAddress);

        foreach (var origin in new[] { "https://app.example.com", "https://staging.example.com" })
        {
            var allowed = await PreflightAsync(http, origin);
            Assert.Equal(origin, Assert.Single(allowed.Headers.GetValues("Access-Control-Allow-Origin")));
        }

        // The dev origin is only allowed when configured.
        var devOrigin = await PreflightAsync(http, WaxyCandlesApiFactory.FrontendOrigin);
        Assert.False(devOrigin.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Rejected_requests_from_the_frontend_still_carry_cors_headers()
    {
        // CORS runs before authentication, so the browser can read the 401.
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/alerts");
        request.Headers.Add("Origin", WaxyCandlesApiFactory.FrontendOrigin);

        var response = await NewRawClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(
            WaxyCandlesApiFactory.FrontendOrigin,
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    private Task<HttpResponseMessage> PreflightAsync(string origin) =>
        PreflightAsync(NewRawClient(), origin);

    private static Task<HttpResponseMessage> PreflightAsync(HttpClient http, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/alerts");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");

        return http.SendAsync(request);
    }
}
