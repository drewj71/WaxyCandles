namespace WaxyCandles.Application.Watchlists.Models;

public sealed class CreateWatchlistRequest
{
    public string? Name { get; set; }
}

public sealed class RenameWatchlistRequest
{
    public string? Name { get; set; }
}

public sealed class AddWatchlistSymbolRequest
{
    public string? Symbol { get; set; }
}

/// <summary>Every one of the user's list ids, in the new order.</summary>
public sealed class ReorderWatchlistsRequest
{
    public List<Guid>? Ids { get; set; }
}

/// <summary>Every symbol in the list, in the new order.</summary>
public sealed class ReorderWatchlistSymbolsRequest
{
    public List<string>? Symbols { get; set; }
}
