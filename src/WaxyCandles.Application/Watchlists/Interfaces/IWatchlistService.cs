using WaxyCandles.Application.Watchlists.Models;

namespace WaxyCandles.Application.Watchlists.Interfaces;

public interface IWatchlistService
{
    Task<IReadOnlyList<string>> GetWatchlistAsync(
        string userId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WatchlistStockDto>> GetWatchlistWithQuotesAsync(
        string userId,
        CancellationToken cancellationToken = default);

    Task AddToWatchlistAsync(
        string userId,
        string symbol,
        CancellationToken cancellationToken = default);

    Task RemoveFromWatchlistAsync(
        string userId,
        string symbol,
        CancellationToken cancellationToken = default);
}