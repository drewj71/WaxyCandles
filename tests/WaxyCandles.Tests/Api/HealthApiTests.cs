using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using WaxyCandles.Api.Health;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Api;

/// <summary>
/// /health/live and /health/ready on the real API against the test containers,
/// and with the database or broker made unreachable.
/// </summary>
public class HealthApiTests(IntegrationFixture fixture) : ApiTestBase(fixture)
{
    [Fact]
    public async Task Ready_is_healthy_when_the_database_and_broker_are_up()
    {
        var (status, body) = await GetAsync(Factory, HealthEndpoints.ReadyPath);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        Assert.Equal("Healthy", CheckStatus(body, HealthEndpoints.DatabaseCheck));
        Assert.Equal("Healthy", CheckStatus(body, HealthEndpoints.RabbitMqCheck));
    }

    [Fact]
    public async Task Live_checks_no_dependencies()
    {
        var (status, body) = await GetAsync(Factory, HealthEndpoints.LivePath);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        Assert.Empty(body.GetProperty("checks").EnumerateObject());
    }

    [Fact]
    public async Task Ready_is_unhealthy_when_the_database_is_unavailable()
    {
        var settings = Settings();
        settings["ConnectionStrings:DefaultConnection"] = new SqlConnectionStringBuilder(ConnectionString)
        {
            InitialCatalog = $"WaxyCandlesMissing_{Guid.NewGuid():N}"
        }.ConnectionString;

        await using var factory = new WaxyCandlesApiFactory(settings, MarketData);

        var (status, body) = await GetAsync(factory, HealthEndpoints.ReadyPath);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("Unhealthy", body.GetProperty("status").GetString());
        Assert.Equal("Unhealthy", CheckStatus(body, HealthEndpoints.DatabaseCheck));
        Assert.Equal("Healthy", CheckStatus(body, HealthEndpoints.RabbitMqCheck));

        // Liveness doesn't depend on it.
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(factory, HealthEndpoints.LivePath)).Status);
    }

    [Fact]
    public async Task Ready_is_unhealthy_when_the_broker_is_unreachable()
    {
        var settings = Settings();
        settings["RabbitMQ:Host"] = "127.0.0.1";
        settings["RabbitMQ:Port"] = UnusedPort().ToString();

        await using var factory = new WaxyCandlesApiFactory(settings, MarketData);

        var (status, body) = await GetAsync(factory, HealthEndpoints.ReadyPath);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("Unhealthy", CheckStatus(body, HealthEndpoints.RabbitMqCheck));
        Assert.Equal("Healthy", CheckStatus(body, HealthEndpoints.DatabaseCheck));

        // Only the failure type is shown on this anonymous endpoint.
        var description = body.GetProperty("checks").GetProperty(HealthEndpoints.RabbitMqCheck)
            .GetProperty("description").GetString();
        Assert.StartsWith("RabbitMQ broker is unreachable", description);

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(factory, HealthEndpoints.LivePath)).Status);
    }

    [Fact]
    public async Task Health_endpoints_are_anonymous_and_not_rate_limited()
    {
        var settings = Settings();

        foreach (var policy in new[] { "MarketData", "HistoryImport", "Auth" })
        {
            settings[$"RateLimiting:{policy}:PermitLimit"] = "1";
        }

        await using var factory = new WaxyCandlesApiFactory(settings, MarketData);

        // No cookies (anonymous), from the web app's origin, many times over.
        for (var i = 0; i < 5; i++)
        {
            foreach (var path in new[] { HealthEndpoints.LivePath, HealthEndpoints.ReadyPath })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.Add("Origin", WaxyCandlesApiFactory.FrontendOrigin);

                var response = await factory.CreateDefaultClient(BaseAddress).SendAsync(request);

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            }
        }
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(
        WaxyCandlesApiFactory factory,
        string path)
    {
        var response = await factory.CreateDefaultClient(BaseAddress).GetAsync(path);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        return (response.StatusCode, body);
    }

    private static string? CheckStatus(JsonElement body, string check) =>
        body.GetProperty("checks").GetProperty(check).GetProperty("status").GetString();

    // A port nothing listens on: bound once to get a free one, then released.
    private static int UnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
