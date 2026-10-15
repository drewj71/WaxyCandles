using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WaxyCandles.Application.MarketData.Interfaces;
using WaxyCandles.Infrastructure;
using WaxyCandles.Infrastructure.MarketData.Calendars;
using WaxyCandles.Infrastructure.MarketData.Providers;

namespace WaxyCandles.Tests.MarketData;

/// <summary>MarketData:Provider picks the IMarketDataProvider AddInfrastructure registers.</summary>
public class MarketDataProviderSelectionTests
{
    [Theory]
    [InlineData(null, typeof(AlphaVantageMarketDataProvider), typeof(WeekdayMarketCalendar))]
    [InlineData("AlphaVantage", typeof(AlphaVantageMarketDataProvider), typeof(WeekdayMarketCalendar))]
    [InlineData("MarketDataApp", typeof(MarketDataAppMarketDataProvider), typeof(MarketDataAppMarketCalendar))]
    public void Configured_provider_and_its_calendar_are_registered(
        string? provider,
        Type expectedProvider,
        Type expectedCalendar)
    {
        using var services = BuildServices(provider);

        Assert.IsType(expectedProvider, services.GetRequiredService<IMarketDataProvider>());
        Assert.IsType(expectedCalendar, services.GetRequiredService<IMarketCalendar>());
        // One instance, so its cache survives between polls.
        Assert.Same(
            services.GetRequiredService<IMarketCalendar>(),
            services.GetRequiredService<IMarketCalendar>());
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
        services.AddLogging();
        services.AddInfrastructure(configuration);

        return services.BuildServiceProvider();
    }
}
