namespace WaxyCandles.Domain.Entities;

/// <summary>One symbol in a <see cref="Watchlist"/>. Unique per list.</summary>
public class WatchlistItem
{
    public Guid Id { get; set; }

    public Guid WatchlistId { get; set; }

    /// <summary>Trimmed and upper-cased.</summary>
    public string Symbol { get; set; } = default!;

    /// <summary>Position within the list, ascending.</summary>
    public int SortOrder { get; set; }

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}
