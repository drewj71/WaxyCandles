namespace WaxyCandles.Infrastructure.Retention;

/// <summary>"Retention" configuration section. Every value is optional.</summary>
public class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>
    /// Kept below SQL Server's lock escalation threshold (about 5,000 locks
    /// per object per statement). Deletes also lock index rows, so stay near
    /// the default rather than at this cap.
    /// </summary>
    public const int MaxBatchSize = 4000;

    /// <summary>
    /// Upper bound for <see cref="Interval"/> and <see cref="InitialDelay"/>,
    /// well inside what <c>Task.Delay</c> accepts (about 49.7 days).
    /// </summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromDays(30);

    /// <summary>
    /// Upper bound for every retention period, so <c>now - retention</c>
    /// can't overflow.
    /// </summary>
    public static readonly TimeSpan MaxRetention = TimeSpan.FromDays(3650);

    /// <summary>Time between retention passes.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Wait after start-up before the first pass.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Published outbox rows are deleted once <c>ProcessedAt</c> is older than
    /// this. Pending rows are never deleted.
    /// </summary>
    public TimeSpan OutboxMessageRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Consumer idempotency claims are deleted once <c>ProcessedAt</c> is older
    /// than this. Must be far longer than any redelivery of the same message
    /// can arrive (retries, requeues and outbox republishes take seconds to
    /// minutes); a dead-letter replay must stay inside it too.
    /// </summary>
    public TimeSpan ProcessedMessageRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Stock prices are deleted once <c>Timestamp</c> is older than this,
    /// except the latest price of each symbol, which is always kept.
    /// </summary>
    public TimeSpan StockPriceRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Refresh tokens are deleted once both <c>ExpiresAt</c> and
    /// <c>RevokedAt</c> (if set) are older than this. Counting from expiry,
    /// not revocation, keeps a rotated token for its whole 30-day lifetime, so
    /// replaying it is still recognised as reuse and revokes its family; the
    /// extra days only cover clock skew and a delayed pass. Nothing reads a
    /// token after that: the browser cookie expires with it.
    /// </summary>
    public TimeSpan RefreshTokenRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Rows deleted per statement.</summary>
    public int BatchSize { get; set; } = 1000;
}
