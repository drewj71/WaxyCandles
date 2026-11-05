using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace WaxyCandles.Infrastructure.LivePrices;

public static class LivePricesServiceCollectionExtensions
{
    /// <summary>
    /// The subscription registry and the broadcaster loop, with their options
    /// (validated on start). The host adds the
    /// <see cref="Application.MarketData.LivePrices.ILivePriceNotifier"/> that
    /// reaches its clients (the API's SignalR hub).
    /// </summary>
    public static IServiceCollection AddLivePrices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<LivePriceSubscriptions>();

        services
            .AddOptions<LivePricesOptions>()
            .Bind(configuration.GetSection(LivePricesOptions.SectionName))
            .Validate(
                x => x.PollInterval >= LivePricesOptions.MinPollInterval &&
                     x.PollInterval <= LivePricesOptions.MaxPollInterval,
                $"LivePrices:PollInterval must be between {LivePricesOptions.MinPollInterval.TotalMilliseconds} ms " +
                $"and {LivePricesOptions.MaxPollInterval.TotalSeconds} s.")
            .Validate(
                x => x.MaxSubscriptionsPerConnection is > 0 and <= LivePricesOptions.MaxSubscriptionsLimit,
                $"LivePrices:MaxSubscriptionsPerConnection must be between 1 and {LivePricesOptions.MaxSubscriptionsLimit}.")
            .ValidateOnStart();

        services.AddHostedService<LivePriceBroadcaster>();

        return services;
    }
}
