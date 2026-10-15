using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WaxyCandles.Application.MarketData.Interfaces;
using WaxyCandles.Infrastructure;
using WaxyCandles.Infrastructure.MarketData.Providers;

namespace WaxyCandles.Tests.MarketData;

/// <summary>MarketData:Provider picks the IMarketDataProvider AddInfrastructure registers.</summary>
public class MarketDataProviderSelectionTests
{
    [Theory]
    [InlineData(null, typeof(AlphaVantageMarketDataProvider))]
    [InlineData("AlphaVantage", typeof(AlphaVantageMarketDataProvider))]
    [InlineData("MarketDataApp", typeof(MarketDataAppMarketDataProvider))]
    public void Configured_provider_is_registered(string? provider, Type expected)
    {
        using var services = BuildServices(provider);

        Assert.IsType(expected, services.GetRequiredService<IMarketDataProvider>());
    }

    [Fact]
    public void Unknown_provider_fails_at_startup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildServices("Schwab"));

        Assert.Contains("MarketData:Provider", ex.Message);
    }

    private static ServiceProvider BuildServices(string? provider)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketData:Provider"] = provider
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddInfrastructure(configuration);

        return services.BuildServiceProvider();
    }
}
