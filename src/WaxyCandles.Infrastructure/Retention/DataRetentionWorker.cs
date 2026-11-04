using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WaxyCandles.Infrastructure.Retention;

/// <summary>
/// Runs a retention pass every <see cref="RetentionOptions.Interval"/>, the
/// first after <see cref="RetentionOptions.InitialDelay"/>. A failure is
/// logged and retried next pass; it never stops the host, and one table
/// failing doesn't skip the others.
/// </summary>
public class DataRetentionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DataRetentionWorker> _logger;
    private readonly RetentionOptions _options;
    private readonly TimeProvider _timeProvider;

    public DataRetentionWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<DataRetentionWorker> logger,
        IOptions<RetentionOptions> options,
        TimeProvider timeProvider)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        // Let host startup finish before the first database call.
        await Task.Yield();

        try
        {
            await Task.Delay(
                _options.InitialDelay,
                _timeProvider,
                stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await RunPassAsync(stoppingToken);

                await Task.Delay(
                    _options.Interval,
                    _timeProvider,
                    stoppingToken);
            }
        }
        // Shutting down. A delete cancelled mid-statement usually surfaces as a
        // SqlException ("Operation cancelled by user") rather than an
        // OperationCanceledException, so don't filter on the type.
        catch (Exception)
            when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>One pass over every table. Never throws except on cancellation.</summary>
    public async Task<RetentionPassResult> RunPassAsync(
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var batchSize = _options.BatchSize;

        var outbox = await DeleteAsync(
            "OutboxMessages",
            now - _options.OutboxMessageRetention,
            (service, cutoff) => service.DeleteProcessedOutboxMessagesAsync(cutoff, batchSize, cancellationToken),
            cancellationToken);

        var processedMessages = await DeleteAsync(
            "ProcessedMessages",
            now - _options.ProcessedMessageRetention,
            (service, cutoff) => service.DeleteProcessedMessagesAsync(cutoff, batchSize, cancellationToken),
            cancellationToken);

        var stockPrices = await DeleteAsync(
            "StockPrices",
            now - _options.StockPriceRetention,
            (service, cutoff) => service.DeleteOldStockPricesAsync(cutoff, batchSize, cancellationToken),
            cancellationToken);

        var refreshTokens = await DeleteAsync(
            "RefreshTokens",
            now - _options.RefreshTokenRetention,
            (service, cutoff) => service.DeleteOldRefreshTokensAsync(cutoff, batchSize, cancellationToken),
            cancellationToken);

        return new RetentionPassResult(outbox, processedMessages, stockPrices, refreshTokens);
    }

    /// <returns>Rows deleted, or null if the table's deletes failed.</returns>
    private async Task<int?> DeleteAsync(
        string table,
        DateTimeOffset cutoff,
        Func<DataRetentionService, DateTimeOffset, Task<int>> delete,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();

            var service = scope.ServiceProvider
                .GetRequiredService<DataRetentionService>();

            var deleted = await delete(service, cutoff);

            _logger.LogInformation(
                "Retention deleted {Count} row(s) from {Table} older than {Cutoff:u}.",
                deleted,
                table,
                cutoff);

            return deleted;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Batches already committed stay deleted; the rest is retried next pass.
            _logger.LogError(
                ex,
                "Retention failed for {Table}; retrying next pass.",
                table);

            return null;
        }
    }
}

/// <summary>Rows deleted per table in one pass; null where that table failed.</summary>
public record RetentionPassResult(
    int? OutboxMessages,
    int? ProcessedMessages,
    int? StockPrices,
    int? RefreshTokens);
