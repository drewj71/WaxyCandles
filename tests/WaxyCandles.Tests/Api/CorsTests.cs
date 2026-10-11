using System.Net;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Api;

/// <summary>
/// Cookie auth only works cross-origin if CORS allows credentials for exactly the
/// Vite dev origin, and no other.
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

    private Task<HttpResponseMessage> PreflightAsync(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/alerts");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");

        return NewRawClient().SendAsync(request);
    }
}
