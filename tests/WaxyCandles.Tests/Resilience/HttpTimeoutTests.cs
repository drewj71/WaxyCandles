using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WaxyCandles.Application.MarketData.Exceptions;
using WaxyCandles.Application.MarketData.Interfaces;
using WaxyCandles.Application.Options.Interfaces;
using WaxyCandles.Application.Options.Models;
using WaxyCandles.Infrastructure;
using WaxyCandles.Tests.Telemetry;
using static WaxyCandles.Infrastructure.Telemetry.WaxyCandlesMetrics;

namespace WaxyCandles.Tests.Resilience;

/// <summary>
/// REL-3: every vendor HttpClient has an explicit timeout, and a timed-out
/// request is never retried (Market Data bills answered requests). In the
/// Metrics collection because it records waxycandles.marketdata.requests.
/// </summary>
[Collection(MetricsCollection.Name)]
public sealed class HttpTimeoutTests : IDisposable
{
    // Named in DependencyInjection / MarketDataAppApi (internal).
    private const string MarketDataClient = "MarketDataApp";
    private const string MarketDataOptionsClient = "MarketDataApp.Options";

    private readonly MetricRecorder _metrics = new();

    public void Dispose() => _metrics.Dispose();

    [Fact]
    public void Market_data_clients_have_the_default_timeouts()
    {
        using var services = Build(new() { ["MarketData:Provider"] = "MarketDataApp" });
        var factory = services.GetRequiredService<IHttpClientFactory>();

        // Prices, candles and the market calendar share the first client.
        Assert.Equal(TimeSpan.FromSeconds(20), factory.CreateClient(MarketDataClient).Timeout);
        Assert.Equal(TimeSpan.FromSeconds(30), factory.CreateClient(MarketDataOptionsClient).Timeout);
    }

    [Fact]
    public void Alpha_vantage_client_has_the_default_timeout()
    {
        using var services = Build([]);
        var factory = services.GetRequiredService<IHttpClientFactory>();

        Assert.Equal(
            TimeSpan.FromSeconds(20),
            factory.CreateClient(DependencyInjection.AlphaVantageHttpClientName).Timeout);
    }

    [Fact]
    public void Timeouts_can_be_configured()
    {
        using var services = Build(new()
        {
            ["MarketData:Provider"] = "MarketDataApp",
            ["MarketDataApp:Timeout"] = "00:00:07",
            ["MarketDataApp:OptionsTimeout"] = "00:00:45",
            ["AlphaVantage:Timeout"] = "00:00:12"
        });
        var factory = services.GetRequiredService<IHttpClientFactory>();

        Assert.Equal(TimeSpan.FromSeconds(7), factory.CreateClient(MarketDataClient).Timeout);
        Assert.Equal(TimeSpan.FromSeconds(45), factory.CreateClient(MarketDataOptionsClient).Timeout);

        using var alphaVantage = Build(new() { ["AlphaVantage:Timeout"] = "00:00:12" });
        Assert.Equal(
            TimeSpan.FromSeconds(12),
            alphaVantage.GetRequiredService<IHttpClientFactory>()
                .CreateClient(DependencyInjection.AlphaVantageHttpClientName).Timeout);
    }

    [Fact]
    public async Task Quote_request_that_times_out_is_not_retried_and_is_recorded_as_a_timeout()
    {
        var handler = new StallingHandler();

        using var services = Build(
            new()
            {
                ["MarketData:Provider"] = "MarketDataApp",
                ["MarketDataApp:Timeout"] = "00:00:00.200"
            },
            s => s.AddHttpClient(MarketDataClient).ConfigurePrimaryHttpMessageHandler(() => handler));

        var provider = services.GetRequiredService<IMarketDataProvider>();

        var ex = await Assert.ThrowsAsync<MarketDataUnavailableException>(
            () => provider.GetCurrentQuotesAsync(["AAPL"]));

        Assert.Equal(MarketDataUnavailableReason.Timeout, ex.Reason);
        Assert.IsType<TimeoutException>(Assert.IsType<TaskCanceledException>(ex.InnerException).InnerException);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(1, _metrics.Sum(
            "waxycandles.marketdata.requests",
            (EndpointTag, "prices"),
            (OutcomeTag, "timeout")));
    }

    [Fact]
    public async Task Option_chain_request_that_times_out_is_not_retried()
    {
        var handler = new StallingHandler();

        using var services = Build(
            new() { ["MarketDataApp:OptionsTimeout"] = "00:00:00.200" },
            s => s.AddHttpClient(MarketDataOptionsClient).ConfigurePrimaryHttpMessageHandler(() => handler));

        var provider = services.GetRequiredService<IOptionsDataProvider>();

        var ex = await Assert.ThrowsAsync<MarketDataUnavailableException>(
            () => provider.GetOptionChainAsync(new OptionChainQuery(
                "AAPL",
                DateOnly.FromDateTime(DateTime.UtcNow),
                DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)),
                240m,
                260m)));

        Assert.Equal(MarketDataUnavailableReason.Timeout, ex.Reason);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Rate_limited_request_is_not_retried()
    {
        var handler = new StallingHandler { Respond = HttpStatusCode.TooManyRequests };

        using var services = Build(
            new() { ["MarketData:Provider"] = "MarketDataApp" },
            s => s.AddHttpClient(MarketDataClient).ConfigurePrimaryHttpMessageHandler(() => handler));

        var ex = await Assert.ThrowsAsync<MarketDataUnavailableException>(
            () => services.GetRequiredService<IMarketDataProvider>().GetCurrentQuotesAsync(["AAPL"]));

        Assert.Equal(MarketDataUnavailableReason.RateLimited, ex.Reason);

        Assert.Equal(1, handler.Calls);
    }

    private static ServiceProvider Build(
        Dictionary<string, string?> settings,
        Action<IServiceCollection>? configure = null)
    {
        // A token for the fake handler only; nothing is sent anywhere.
        settings.TryAdd("MarketDataApp:ApiToken", "test-token-not-a-secret");
        settings.TryAdd("ConnectionStrings:DefaultConnection", "Server=unused;Database=unused");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddInfrastructure(configuration);
        configure?.Invoke(services);

        return services.BuildServiceProvider();
    }

    /// <summary>Never answers until cancelled, or answers with a fixed status.</summary>
    private sealed class StallingHandler : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public HttpStatusCode? Respond { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);

            if (Respond is { } status)
            {
                return new HttpResponseMessage(status)
                {
                    Content = new StringContent("""{ "s": "error", "errmsg": "Rate limited." }""")
                };
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }
}
