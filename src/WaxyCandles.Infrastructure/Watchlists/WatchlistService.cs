using Microsoft.EntityFrameworkCore;
using WaxyCandles.Application.MarketData.Interfaces;
using WaxyCandles.Application.Watchlists.Interfaces;
using WaxyCandles.Application.Watchlists.Models;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Infrastructure.Persistence;

namespace WaxyCandles.Infrastructure.Watchlists;

public class WatchlistService : IWatchlistService
{
    private readonly AppDbContext _dbContext;

    public WatchlistService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<string>> GetWatchlistAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.Watchlists
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.AddedAt)
            .Select(x => x.Symbol)
            .ToListAsync(cancellationToken);
    }

    public async Task AddToWatchlistAsync(
        string userId,
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var normalizedSymbol =
            symbol.Trim().ToUpperInvariant();

        var exists = await _dbContext.Watchlists
            .AnyAsync(
                x =>
                    x.UserId == userId &&
                    x.Symbol == normalizedSymbol,
                cancellationToken);

        if (exists)
        {
            return;
        }

        var watchlistItem = new Watchlist
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Symbol = normalizedSymbol,
            AddedAt = DateTimeOffset.UtcNow
        };

        _dbContext.Watchlists.Add(watchlistItem);

        await _dbContext.SaveChangesAsync(
            cancellationToken);
    }

    public async Task RemoveFromWatchlistAsync(
        string userId,
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var normalizedSymbol =
            symbol.Trim().ToUpperInvariant();

        var watchlistItem =
            await _dbContext.Watchlists
                .FirstOrDefaultAsync(
                    x =>
                        x.UserId == userId &&
                        x.Symbol == normalizedSymbol,
                    cancellationToken);

        if (watchlistItem is null)
        {
            return;
        }

        _dbContext.Watchlists.Remove(watchlistItem);

        await _dbContext.SaveChangesAsync(
            cancellationToken);
    }

    public async Task<IReadOnlyList<WatchlistStockDto>> GetWatchlistWithQuotesAsync(
    string userId,
    CancellationToken cancellationToken = default)
    {
        var symbols = await _dbContext.Watchlists
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.AddedAt)
            .Select(x => x.Symbol)
            .ToListAsync(cancellationToken);

        if (symbols.Count == 0)
        {
            return [];
        }

        var latestPrices = await _dbContext.StockPrices
            .Where(x => symbols.Contains(x.Symbol))
            .GroupBy(x => x.Symbol)
            .Select(g => g
                .OrderByDescending(x => x.Timestamp)
                .First())
            .ToListAsync(cancellationToken);

        return latestPrices
            .Select(x => new WatchlistStockDto
            {
                Symbol = x.Symbol,
                Price = x.Price,
                Change = x.Change,
                ChangePercent = x.ChangePercent
            })
            .ToList();
    }
}