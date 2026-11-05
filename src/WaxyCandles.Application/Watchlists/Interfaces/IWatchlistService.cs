using WaxyCandles.Application.Watchlists.Models;

namespace WaxyCandles.Application.Watchlists.Interfaces;

/// <summary>
/// A user's named watchlists. Every method is scoped to <c>userId</c>: another
/// user's list id behaves exactly like one that doesn't exist (null/false,
/// which the API turns into 404). Invalid input and exceeded limits throw
/// <see cref="Common.Exceptions.RequestValidationException"/> (400).
/// </summary>
public interface IWatchlistService
{
    Task<IReadOnlyList<WatchlistDto>> GetWatchlistsAsync(
        string userId,
        CancellationToken cancellationToken = default);

    Task<WatchlistDto?> GetWatchlistAsync(
        string userId,
        Guid watchlistId,
        CancellationToken cancellationToken = default);

    Task<WatchlistDto> CreateWatchlistAsync(
        string userId,
        string? name,
        CancellationToken cancellationToken = default);

    Task<WatchlistDto?> RenameWatchlistAsync(
        string userId,
        Guid watchlistId,
        string? name,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteWatchlistAsync(
        string userId,
        Guid watchlistId,
        CancellationToken cancellationToken = default);

    /// <summary><paramref name="orderedIds"/> must be exactly the user's list ids.</summary>
    Task ReorderWatchlistsAsync(
        string userId,
        IReadOnlyList<Guid>? orderedIds,
        CancellationToken cancellationToken = default);

    /// <summary>Appends the symbol; adding one already in the list is a no-op.</summary>
    Task<WatchlistDto?> AddSymbolAsync(
        string userId,
        Guid watchlistId,
        string? symbol,
        CancellationToken cancellationToken = default);

    /// <summary>False only if the list isn't found; a symbol not in it is a no-op.</summary>
    Task<bool> RemoveSymbolAsync(
        string userId,
        Guid watchlistId,
        string? symbol,
        CancellationToken cancellationToken = default);

    /// <summary><paramref name="orderedSymbols"/> must be exactly the list's symbols.</summary>
    Task<WatchlistDto?> ReorderSymbolsAsync(
        string userId,
        Guid watchlistId,
        IReadOnlyList<string>? orderedSymbols,
        CancellationToken cancellationToken = default);

    /// <summary>The list's quote table, in list order; null if the list isn't found.</summary>
    Task<IReadOnlyList<WatchlistQuoteDto>?> GetQuotesAsync(
        string userId,
        Guid watchlistId,
        CancellationToken cancellationToken = default);

    // ---- Single-list API (/api/watchlist), kept for existing clients ----
    // These act on the user's default list: the first by sort order.

    Task<IReadOnlyList<string>> GetDefaultWatchlistSymbolsAsync(
        string userId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WatchlistStockDto>> GetDefaultWatchlistQuotesAsync(
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>Creates the default list ("My Watchlist") if the user has none.</summary>
    Task AddToDefaultWatchlistAsync(
        string userId,
        string symbol,
        CancellationToken cancellationToken = default);

    Task RemoveFromDefaultWatchlistAsync(
        string userId,
        string symbol,
        CancellationToken cancellationToken = default);
}
