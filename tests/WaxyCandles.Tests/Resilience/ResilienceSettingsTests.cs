using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WaxyCandles.Infrastructure;
using WaxyCandles.Infrastructure.Resilience;

namespace WaxyCandles.Tests.Resilience;

/// <summary>The REL-2/REL-3 settings: optional, with defaults, and a bad value stops startup naming the key.</summary>
public class ResilienceSettingsTests
{
    [Fact]
    public void Defaults_apply_when_unset()
    {
        var configuration = Configuration([]);

        Assert.Equal(
            new DatabaseRetrySettings(3, TimeSpan.FromSeconds(5)),
            DatabaseRetrySettings.From(configuration));

        Assert.Equal(
            new HttpClientTimeouts(
                TimeSpan.FromSeconds(20),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(20)),
            HttpClientTimeouts.From(configuration));
    }

    [Fact]
    public void Configured_values_are_used()
    {
        var configuration = Configuration(new()
        {
            ["Database:MaxRetryCount"] = "0",
            ["Database:MaxRetryDelay"] = "00:00:30"
        });

        Assert.Equal(
            new DatabaseRetrySettings(0, TimeSpan.FromSeconds(30)),
            DatabaseRetrySettings.From(configuration));
    }

    [Theory]
    [InlineData("Database:MaxRetryCount", "-1")]
    [InlineData("Database:MaxRetryCount", "11")]
    [InlineData("Database:MaxRetryCount", "three")]
    [InlineData("Database:MaxRetryDelay", "00:00:00")]
    [InlineData("Database:MaxRetryDelay", "00:00:31")]
    [InlineData("Database:MaxRetryDelay", "soon")]
    [InlineData("MarketDataApp:Timeout", "00:00:00")]
    [InlineData("MarketDataApp:Timeout", "00:02:01")]
    [InlineData("MarketDataApp:OptionsTimeout", "-00:00:01")]
    [InlineData("AlphaVantage:Timeout", "soon")]
    public void Invalid_value_stops_startup_naming_the_key(string key, string value)
    {
        var configuration = Configuration(new() { [key] = value });

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddInfrastructure(configuration));

        Assert.Contains(key, ex.Message);
    }

    private static IConfiguration Configuration(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
}
