using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace WaxyCandles.Infrastructure.Retention;

public static class RetentionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the retention pass and its options (validated on start).
    /// Add the hosted <see cref="DataRetentionWorker"/> separately, in the
    /// one process that should run it (the OutboxWorker).
    /// </summary>
    public static IServiceCollection AddDataRetention(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<DataRetentionService>();

        services
            .AddOptions<RetentionOptions>()
            .Bind(configuration.GetSection(RetentionOptions.SectionName))
            // Upper bounds too: an out-of-range delay or retention would throw
            // outside the per-table error handling and stop the host.
            .Validate(
                x => x.Interval > TimeSpan.Zero && x.Interval <= RetentionOptions.MaxDelay,
                $"Retention:Interval must be greater than zero and at most {RetentionOptions.MaxDelay.TotalDays} days.")
            .Validate(
                x => x.InitialDelay >= TimeSpan.Zero && x.InitialDelay <= RetentionOptions.MaxDelay,
                $"Retention:InitialDelay must not be negative and at most {RetentionOptions.MaxDelay.TotalDays} days.")
            .Validate(
                x => IsValidRetention(x.OutboxMessageRetention),
                RetentionMessage("OutboxMessageRetention"))
            .Validate(
                x => IsValidRetention(x.ProcessedMessageRetention),
                RetentionMessage("ProcessedMessageRetention"))
            .Validate(
                x => IsValidRetention(x.StockPriceRetention),
                RetentionMessage("StockPriceRetention"))
            .Validate(
                x => x.BatchSize is > 0 and <= RetentionOptions.MaxBatchSize,
                $"Retention:BatchSize must be between 1 and {RetentionOptions.MaxBatchSize}.")
            .ValidateOnStart();

        return services;
    }

    private static bool IsValidRetention(TimeSpan retention) =>
        retention > TimeSpan.Zero && retention <= RetentionOptions.MaxRetention;

    private static string RetentionMessage(string key) =>
        $"Retention:{key} must be greater than zero and at most {RetentionOptions.MaxRetention.TotalDays} days.";
}
