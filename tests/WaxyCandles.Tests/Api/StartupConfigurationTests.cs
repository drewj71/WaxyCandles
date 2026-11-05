using System.Net;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Api;

/// <summary>
/// Required configuration stops the API at startup with a message that names
/// the key (and never shows a secret), and development-only endpoints stay off
/// elsewhere.
/// </summary>
public class StartupConfigurationTests(IntegrationFixture fixture) : ApiTestBase(fixture)
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Missing_jwt_secret_fails_startup_naming_the_key(string? secret)
    {
        var settings = Settings();
        settings["Jwt:Secret"] = secret;

        var message = await StartupFailureAsync(settings);

        Assert.Contains("Jwt:Secret is not set", message);
    }

    [Fact]
    public async Task Short_jwt_secret_fails_startup_without_showing_it()
    {
        const string shortSecret = "only-31-bytes-long-placeholder!";
        Assert.Equal(31, shortSecret.Length);

        var settings = Settings();
        settings["Jwt:Secret"] = shortSecret;

        var message = await StartupFailureAsync(settings);

        Assert.Contains("Jwt:Secret is too short", message);
        Assert.Contains("32", message);
        Assert.DoesNotContain(shortSecret, message);
    }

    [Fact]
    public async Task Missing_cors_origin_fails_startup_naming_the_key()
    {
        var settings = Settings();
        settings.Remove("Cors:AllowedOrigins:0");

        var message = await StartupFailureAsync(settings);

        Assert.Contains("Cors:AllowedOrigins is not set", message);
    }

    [Fact]
    public async Task Wildcard_cors_origin_fails_startup()
    {
        var settings = Settings();
        settings["Cors:AllowedOrigins:0"] = "*";

        var message = await StartupFailureAsync(settings);

        Assert.Contains("Cors:AllowedOrigins can't be \"*\"", message);
    }

    [Fact]
    public async Task Invalid_rate_limit_fails_startup()
    {
        var settings = Settings();
        settings["RateLimiting:Auth:PermitLimit"] = "0";

        var message = await StartupFailureAsync(settings);

        Assert.Contains("RateLimiting", message);
    }

    [Theory]
    [InlineData("LivePrices:PollInterval", "00:00:00.1")]
    [InlineData("LivePrices:PollInterval", "00:05:00")]
    [InlineData("LivePrices:MaxSubscriptionsPerConnection", "0")]
    public async Task Invalid_live_prices_setting_fails_startup_naming_the_key(string key, string value)
    {
        var settings = Settings();
        settings[key] = value;

        var message = await StartupFailureAsync(settings);

        Assert.Contains(key, message);
    }

    [Theory]
    [InlineData("/swagger/v1/swagger.json")]
    [InlineData("/swagger/index.html")]
    public async Task Swagger_is_not_served_outside_development(string url)
    {
        // The factory runs as "Testing".
        var response = await NewRawClient().GetAsync(url);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Starts an API with these settings and returns the whole failure, inner exceptions included.</summary>
    private async Task<string> StartupFailureAsync(Dictionary<string, string?> settings)
    {
        await using var factory = new WaxyCandlesApiFactory(settings, MarketData);

        var ex = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(ex);
        return ex.ToString();
    }
}
