namespace WaxyCandles.Application.Watchlists.Models;

/// <summary>One of a user's watchlists, with its symbols in list order.</summary>
public sealed class WatchlistDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = default!;

    public int SortOrder { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public IReadOnlyList<string> Symbols { get; set; } = [];
}
