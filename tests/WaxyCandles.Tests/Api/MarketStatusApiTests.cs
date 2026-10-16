using System.Net;
using System.Text.Json;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Api;

/// <summary>GET /api/market/status, which the Dashboard's Market Status card reads.</summary>
public class MarketStatusApiTests(IntegrationFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task Returns_whether_open_and_the_next_change_in_the_future()
    {
        var client = await RegisteredClientAsync("market@example.com");

        var response = await client.Http.GetAsync("/api/market/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        Assert.Contains(root.GetProperty("isOpen").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
        Assert.True(root.GetProperty("nextChange").GetDateTimeOffset() > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Requires_sign_in()
    {
        var response = await NewRawClient().GetAsync("/api/market/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
