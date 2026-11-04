using Microsoft.EntityFrameworkCore;
using WaxyCandles.Infrastructure.Persistence;

namespace WaxyCandles.Infrastructure.Retention;

/// <summary>
/// Deletes rows that nothing reads any more, in batches of at most
/// <c>batchSize</c> per statement. Each statement commits on its own, so locks
/// are held only for one batch and the publisher and consumers aren't blocked
/// for long. Each method returns the number of rows deleted.
/// </summary>
public class DataRetentionService
{
    // Real time on purpose (not TimeProvider): it only spaces out database work.
    private static readonly TimeSpan PauseBetweenBatches = TimeSpan.FromMilliseconds(100);

    private readonly AppDbContext _dbContext;

    public DataRetentionService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// Published outbox rows processed before <paramref name="cutoff"/>.
    /// Pending rows (<c>ProcessedAt IS NULL</c>) never match, at any age.
    /// </summary>
    public Task<int> DeleteProcessedOutboxMessagesAsync(
        DateTimeOffset cutoff,
        int batchSize,
        CancellationToken cancellationToken = default) =>
        DeleteInBatchesAsync(
            // The literal IS NOT NULL lets SQL Server use the filtered
            // IX_OutboxMessages_Processed.
            () => _dbContext.Database.ExecuteSqlAsync(
                $"""
                DELETE TOP ({batchSize}) FROM [OutboxMessages]
                WHERE [ProcessedAt] IS NOT NULL AND [ProcessedAt] < {cutoff}
                """,
                cancellationToken),
            batchSize,
            cancellationToken);

    /// <summary>Consumer idempotency claims made before <paramref name="cutoff"/>.</summary>
    public Task<int> DeleteProcessedMessagesAsync(
        DateTimeOffset cutoff,
        int batchSize,
        CancellationToken cancellationToken = default) =>
        DeleteInBatchesAsync(
            () => _dbContext.Database.ExecuteSqlAsync(
                $"""
                DELETE TOP ({batchSize}) FROM [ProcessedMessages]
                WHERE [ProcessedAt] < {cutoff}
                """,
                cancellationToken),
            batchSize,
            cancellationToken);

    /// <summary>
    /// Stock prices observed before <paramref name="cutoff"/>, except each
    /// symbol's latest price: a row is deleted only if a newer row exists for
    /// its symbol. Every reader of StockPrices only needs the latest per symbol
    /// (watchlist, stock details, the poller's newer-than-latest check and
    /// GEX level refresh's StateChangedAt), so those results never change.
    /// </summary>
    public Task<int> DeleteOldStockPricesAsync(
        DateTimeOffset cutoff,
        int batchSize,
        CancellationToken cancellationToken = default) =>
        DeleteInBatchesAsync(
            // The EXISTS seeks IX_StockPrices_Symbol_Timestamp. The outer
            // Timestamp < cutoff has no Symbol, so it scans that index; fine at
            // ~390 rows per symbol per day. Add a Timestamp index if the first
            // backlog purge or many symbols make it slow.
            () => _dbContext.Database.ExecuteSqlAsync(
                $"""
                DELETE TOP ({batchSize}) p
                FROM [StockPrices] AS p
                WHERE p.[Timestamp] < {cutoff}
                  AND EXISTS (
                      SELECT 1 FROM [StockPrices] AS newer
                      WHERE newer.[Symbol] = p.[Symbol]
                        AND newer.[Timestamp] > p.[Timestamp])
                """,
                cancellationToken),
            batchSize,
            cancellationToken);

    /// <summary>
    /// Refresh tokens whose expiry, and revocation if any, are both before
    /// <paramref name="cutoff"/>. Revocation alone never makes a row eligible:
    /// a rotated token is kept until after it expires, so replaying it (reuse
    /// detection, which revokes its family) keeps working for its whole
    /// lifetime instead of turning into an unknown token.
    /// </summary>
    public Task<int> DeleteOldRefreshTokensAsync(
        DateTimeOffset cutoff,
        int batchSize,
        CancellationToken cancellationToken = default) =>
        DeleteInBatchesAsync(
            // Seeks IX_RefreshTokens_ExpiresAt.
            () => _dbContext.Database.ExecuteSqlAsync(
                $"""
                DELETE TOP ({batchSize}) FROM [RefreshTokens]
                WHERE [ExpiresAt] < {cutoff}
                  AND ([RevokedAt] IS NULL OR [RevokedAt] < {cutoff})
                """,
                cancellationToken),
            batchSize,
            cancellationToken);

    private static async Task<int> DeleteInBatchesAsync(
        Func<Task<int>> deleteBatch,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var total = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var deleted = await deleteBatch();
            total += deleted;

            // A short batch means nothing eligible is left.
            if (deleted < batchSize)
            {
                return total;
            }

            // More to delete (e.g. the first pass over a large backlog): pause
            // so back-to-back batches don't crowd out the publisher and
            // consumers or grow the transaction log in one burst.
            await Task.Delay(PauseBetweenBatches, cancellationToken);
        }
    }
}
