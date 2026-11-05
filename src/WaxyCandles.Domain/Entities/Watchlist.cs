namespace WaxyCandles.Domain.Entities;

/// <summary>A user's named list of symbols. A user can have several.</summary>
public class Watchlist
{
    public const int NameMaxLength = 50;

    public Guid Id { get; set; }

    public string UserId { get; set; } = default!;

    /// <summary>Unique per user (case-insensitive, by the database collation).</summary>
    public string Name { get; set; } = default!;

    /// <summary>Position among the user's lists, ascending.</summary>
    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<WatchlistItem> Items { get; set; } = [];
}
