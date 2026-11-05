using Microsoft.EntityFrameworkCore;
using WaxyCandles.Application.MarketData;
using WaxyCandles.Application.Watchlists.Interfaces;
using WaxyCandles.Application.Watchlists.Models;
using WaxyCandles.Application.Watchlists.Validation;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure.MarketData;
using WaxyCandles.Infrastructure.Persistence;

namespace WaxyCandles.Infrastructure.Watchlists;

/// <summary>
/// Named watchlists. Every query filters by the caller's user id, so another
/// user's list is indistinguishable from a missing one.
/// </summary>
/// <remarks>
/// The limits are checked before the insert, so two concurrent requests can
/// each pass the check and exceed one by a row. Uniqueness (a name per user, a
/// symbol per list) is enforced by unique indexes, not by the checks.
/// </remarks>
public class WatchlistService : IWatchlistService
{
    /// <summary>How far back the sparkline's daily candles go.</summary>
    public static readonly TimeSpan SparklineWindow = TimeSpan.FromDays(30);

    /// <summary>
    /// A session's prices are within this long before its latest price: the
    /// regular session is 6.5 hours, and the previous one closed at least
    /// 17.5 hours before the next opens.
    /// </summary>
    public static readonly TimeSpan DayRangeWindow = TimeSpan.FromHours(12);

    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public WatchlistService(AppDbContext dbContext, TimeProvider? timeProvider = null)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // ---- Lists ----

    public async Task<IReadOnlyList<WatchlistDto>> GetWatchlistsAsync(
        string userId,
        CancellationToken cancellationToken = default) =>
        await ToDtos(UserLists(userId)).ToListAsync(cancellationToken);

    public Task<WatchlistDto?> GetWatchlistAsync(
        string userId,
        Guid watchlistId,
        CancellationToken cancellationToken = default) =>
        ToDtos(UserLists(userId).Where(x => x.Id == watchlistId))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<WatchlistDto> CreateWatchlistAsync(
        string userId,
        string? name,
        CancellationToken cancellationToken = default)
    {
        var validName = WatchlistRules.ValidateName(name);

        var existing = await _dbContext.Watchlists
            .Where(x => x.UserId == userId)
            .Select(x => new { x.Name, x.SortOrder })
            .ToListAsync(cancellationToken);

        if (existing.Count >= WatchlistRules.MaxListsPerUser)
        {
            throw WatchlistRules.Invalid(
                "name",
                $"You can have at most {WatchlistRules.MaxListsPerUser} watchlists.");
        }

        if (existing.Any(x => string.Equals(x.Name, validName, StringComparison.OrdinalIgnoreCase)))
        {
            throw DuplicateName();
        }

        var watchlist = new Watchlist
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = validName,
            SortOrder = existing.Count == 0 ? 0 : existing.Max(x => x.SortOrder) + 1,
            CreatedAt = _timeProvider.GetUtcNow()
        };

        _dbContext.Watchlists.Add(watchlist);

        try
        {
            // The id is set here, so an ambiguous commit is checked for, not
            // retried into a duplicate.
            await _dbContext.SaveChangesVerifiedAsync(
                ct => _dbContext.Watchlists.AnyAsync(x => x.Id == watchlist.Id, ct),
                cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueConstraintViolation())
        {
            // A concurrent create with the same name won.
            throw DuplicateName();
        }

        return ToDto(watchlist);
    }

    public async Task<WatchlistDto?> RenameWatchlistAsync(
        string userId,
        Guid watchlistId,
        string? name,
        CancellationToken cancellationToken = default)
    {
        var watchlist = await UserLists(userId)
            .FirstOrDefaultAsync(x => x.Id == watchlistId, cancellationToken);

        if (watchlist is null)
        {
            return null;
        }

        var validName = WatchlistRules.ValidateName(name);

        // The column's collation is case-insensitive, so this matches "tech"
        // against "Tech", as the unique index would.
        var taken = await _dbContext.Watchlists.AnyAsync(
            x => x.UserId == userId && x.Id != watchlistId && x.Name == validName,
            cancellationToken);

        if (taken)
        {
            throw DuplicateName();
        }

        watchlist.Name = validName;

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueConstraintViolation())
        {
            throw DuplicateName();
        }

        return await GetWatchlistAsync(userId, watchlistId, cancellationToken);
    }

    public async Task<bool> DeleteWatchlistAsync(
        string userId,
        Guid watchlistId,
        CancellationToken cancellationToken = default)
    {
        // Its items go with it (cascade delete).
        var deleted = await UserLists(userId)
            .Where(x => x.Id == watchlistId)
            .ExecuteDeleteAsync(cancellationToken);

        return deleted > 0;
    }

    public async Task ReorderWatchlistsAsync(
        string userId,
        IReadOnlyList<Guid>? orderedIds,
        CancellationToken cancellationToken = default)
    {
        var lists = await UserLists(userId).ToListAsync(cancellationToken);

        if (orderedIds is null ||
            orderedIds.Count != lists.Count ||
            orderedIds.Distinct().Count() != lists.Count ||
            !orderedIds.All(id => lists.Any(x => x.Id == id)))
        {
            throw WatchlistRules.Invalid(
                "ids",
                "Send every one of your watchlist ids exactly once, in the new order.");
        }

        for (var i = 0; i < orderedIds.Count; i++)
        {
            lists.Single(x => x.Id == orderedIds[i]).SortOrder = i;
        }

        // Plain updates: setting the same values again is harmless.
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    // ---- Symbols ----

    public async Task<WatchlistDto?> AddSymbolAsync(
        string userId,
        Guid watchlistId,
        string? symbol,
        CancellationToken cancellationToken = default)
    {
        if (!await OwnsAsync(userId, watchlistId, cancellationToken))
        {
            return null;
        }

        var normalized = WatchlistRules.ValidateSymbol(symbol);

        await AddItemAsync(watchlistId, normalized, cancellationToken);

        return await GetWatchlistAsync(userId, watchlistId, cancellationToken);
    }

    public async Task<bool> RemoveSymbolAsync(
        string userId,
        Guid watchlistId,
        string? symbol,
        CancellationToken cancellationToken = default)
    {
        if (!await OwnsAsync(userId, watchlistId, cancellationToken))
        {
            return false;
        }

        // Normalized but not validated, so a symbol stored before validation
        // existed can still be removed.
        var normalized = StockSymbol.Normalize(symbol);

        await _dbContext.WatchlistItems
            .Where(x => x.WatchlistId == watchlistId && x.Symbol == normalized)
            .ExecuteDeleteAsync(cancellationToken);

        return true;
    }

    public async Task<WatchlistDto?> ReorderSymbolsAsync(
        string userId,
        Guid watchlistId,
        IReadOnlyList<string>? orderedSymbols,
        CancellationToken cancellationToken = default)
    {
        if (!await OwnsAsync(userId, watchlistId, cancellationToken))
        {
            return null;
        }

        var items = await _dbContext.WatchlistItems
            .Where(x => x.WatchlistId == watchlistId)
            .ToListAsync(cancellationToken);

        var ordered = (orderedSymbols ?? [])
            .Select(StockSymbol.Normalize)
            .ToList();

        // Stored symbols are normalized; the column compares case-insensitively.
        var bySymbol = items.ToDictionary(x => x.Symbol, StringComparer.OrdinalIgnoreCase);

        if (orderedSymbols is null ||
            ordered.Count != items.Count ||
            ordered.Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Count ||
            !ordered.All(bySymbol.ContainsKey))
        {
            throw WatchlistRules.Invalid(
                "symbols",
                "Send every symbol in the list exactly once, in the new order.");
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            bySymbol[ordered[i]].SortOrder = i;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return await GetWatchlistAsync(userId, watchlistId, cancellationToken);
    }

    // ---- Quotes ----

    public async Task<IReadOnlyList<WatchlistQuoteDto>?> GetQuotesAsync(
        string userId,
        Guid watchlistId,
        CancellationToken cancellationToken = default)
    {
        if (!await OwnsAsync(userId, watchlistId, cancellationToken))
        {
            return null;
        }

        var symbols = await OrderedSymbols(watchlistId)
            .ToListAsync(cancellationToken);

        return await BuildQuotesAsync(symbols, cancellationToken);
    }

    // ---- Single-list API ----

    public async Task<IReadOnlyList<string>> GetDefaultWatchlistSymbolsAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var defaultId = await DefaultListIdAsync(userId, cancellationToken);

        return defaultId is { } id
            ? await OrderedSymbols(id).ToListAsync(cancellationToken)
            : [];
    }

    public async Task<IReadOnlyList<WatchlistStockDto>> GetDefaultWatchlistQuotesAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var defaultId = await DefaultListIdAsync(userId, cancellationToken);

        if (defaultId is not { } id)
        {
            return [];
        }

        var quotes = await BuildQuotesAsync(
            await OrderedSymbols(id).ToListAsync(cancellationToken),
            cancellationToken);

        return quotes
            .Select(x => new WatchlistStockDto
            {
                Symbol = x.Symbol,
                Price = x.Price,
                Change = x.Change,
                ChangePercent = x.ChangePercent
            })
            .ToList();
    }

    public async Task AddToDefaultWatchlistAsync(
        string userId,
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var normalized = WatchlistRules.ValidateSymbol(symbol);

        var id = await DefaultListIdAsync(userId, cancellationToken)
            ?? await CreateDefaultListAsync(userId, cancellationToken);

        await AddItemAsync(id, normalized, cancellationToken);
    }

    public async Task RemoveFromDefaultWatchlistAsync(
        string userId,
        string symbol,
        CancellationToken cancellationToken = default)
    {
        if (await DefaultListIdAsync(userId, cancellationToken) is { } id)
        {
            await RemoveSymbolAsync(userId, id, symbol, cancellationToken);
        }
    }

    // ---- Helpers ----

    private IQueryable<Watchlist> UserLists(string userId) =>
        _dbContext.Watchlists
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.CreatedAt)
            .ThenBy(x => x.Id);

    private IQueryable<string> OrderedSymbols(Guid watchlistId) =>
        _dbContext.WatchlistItems
            .Where(x => x.WatchlistId == watchlistId)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.AddedAt)
            .Select(x => x.Symbol);

    private static IQueryable<WatchlistDto> ToDtos(IQueryable<Watchlist> lists) =>
        lists.Select(x => new WatchlistDto
        {
            Id = x.Id,
            Name = x.Name,
            SortOrder = x.SortOrder,
            CreatedAt = x.CreatedAt,
            Symbols = x.Items
                .OrderBy(i => i.SortOrder)
                .ThenBy(i => i.AddedAt)
                .Select(i => i.Symbol)
                .ToList()
        });

    private static WatchlistDto ToDto(Watchlist watchlist) => new()
    {
        Id = watchlist.Id,
        Name = watchlist.Name,
        SortOrder = watchlist.SortOrder,
        CreatedAt = watchlist.CreatedAt,
        Symbols = watchlist.Items
            .OrderBy(i => i.SortOrder)
            .ThenBy(i => i.AddedAt)
            .Select(i => i.Symbol)
            .ToList()
    };

    private Task<bool> OwnsAsync(string userId, Guid watchlistId, CancellationToken cancellationToken) =>
        _dbContext.Watchlists.AnyAsync(
            x => x.Id == watchlistId && x.UserId == userId,
            cancellationToken);

    private async Task<Guid?> DefaultListIdAsync(string userId, CancellationToken cancellationToken) =>
        await UserLists(userId)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<Guid> CreateDefaultListAsync(string userId, CancellationToken cancellationToken)
    {
        try
        {
            return (await CreateWatchlistAsync(
                userId,
                WatchlistRules.DefaultListName,
                cancellationToken)).Id;
        }
        catch (Application.Common.Exceptions.RequestValidationException)
        {
            // A concurrent first add created it; use whichever list is first now.
            _dbContext.ChangeTracker.Clear();

            return await DefaultListIdAsync(userId, cancellationToken)
                ?? throw new InvalidOperationException("The default watchlist could not be created.");
        }
    }

    /// <summary>Appends a (normalized, valid) symbol; already present is a no-op.</summary>
    private async Task AddItemAsync(Guid watchlistId, string symbol, CancellationToken cancellationToken)
    {
        var items = await _dbContext.WatchlistItems
            .Where(x => x.WatchlistId == watchlistId)
            .Select(x => new { x.Symbol, x.SortOrder })
            .ToListAsync(cancellationToken);

        if (items.Any(x => string.Equals(x.Symbol, symbol, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (items.Count >= WatchlistRules.MaxSymbolsPerList)
        {
            throw WatchlistRules.Invalid(
                "symbol",
                $"A watchlist can hold at most {WatchlistRules.MaxSymbolsPerList} symbols.");
        }

        var item = new WatchlistItem
        {
            Id = Guid.NewGuid(),
            WatchlistId = watchlistId,
            Symbol = symbol,
            SortOrder = items.Count == 0 ? 0 : items.Max(x => x.SortOrder) + 1,
            AddedAt = _timeProvider.GetUtcNow()
        };

        _dbContext.WatchlistItems.Add(item);

        try
        {
            await _dbContext.SaveChangesVerifiedAsync(
                ct => _dbContext.WatchlistItems.AnyAsync(x => x.Id == item.Id, ct),
                cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueConstraintViolation())
        {
            // A concurrent add of the same symbol won: the symbol is in the
            // list, which is all the caller asked for.
            _dbContext.Entry(item).State = EntityState.Detached;
        }
    }

    private async Task<IReadOnlyList<WatchlistQuoteDto>> BuildQuotesAsync(
        IReadOnlyList<string> symbols,
        CancellationToken cancellationToken)
    {
        if (symbols.Count == 0)
        {
            return [];
        }

        var latestPrices = await LatestStockPrices.For(_dbContext, symbols)
            .ToListAsync(cancellationToken);

        var latestBySymbol = latestPrices.ToDictionary(
            x => x.Symbol,
            StringComparer.OrdinalIgnoreCase);

        // The range of each symbol's prices in the hours up to its latest one:
        // a seek per symbol on the (Symbol, Timestamp) index.
        var windowHours = -DayRangeWindow.TotalHours;

        var ranges = await (
                from price in _dbContext.StockPrices
                join latest in LatestStockPrices.For(_dbContext, symbols)
                    on price.Symbol equals latest.Symbol
                where price.Timestamp > latest.Timestamp.AddHours(windowHours)
                group price by price.Symbol
                into g
                select new { Symbol = g.Key, Low = g.Min(x => x.Price), High = g.Max(x => x.Price) })
            .ToDictionaryAsync(
                x => x.Symbol,
                StringComparer.OrdinalIgnoreCase,
                cancellationToken);

        var since = _timeProvider.GetUtcNow() - SparklineWindow;

        var candles = await _dbContext.StockCandles
            .Where(x =>
                symbols.Contains(x.Symbol) &&
                x.Interval == CandleInterval.OneDay &&
                x.Timestamp >= since)
            .OrderBy(x => x.Timestamp)
            .Select(x => new { x.Symbol, x.Timestamp, x.Close, x.Volume })
            .ToListAsync(cancellationToken);

        var candlesBySymbol = candles
            .GroupBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // Every symbol is listed, in list order. One with no stored price yet
        // (newly added, before its first poll) has null price fields.
        return symbols
            .Select(symbol =>
            {
                latestBySymbol.TryGetValue(symbol, out var latest);
                ranges.TryGetValue(symbol, out var range);
                candlesBySymbol.TryGetValue(symbol, out var daily);
                var lastCandle = daily?.LastOrDefault();

                return new WatchlistQuoteDto
                {
                    Symbol = symbol,
                    Price = latest?.Price,
                    Change = latest?.Change,
                    ChangePercent = latest?.ChangePercent,
                    PreviousClose = latest is null ? null : latest.Price - latest.Change,
                    PriceTimestamp = latest?.Timestamp,
                    DayLow = range?.Low,
                    DayHigh = range?.High,
                    Volume = lastCandle?.Volume,
                    VolumeDate = lastCandle?.Timestamp,
                    Sparkline = daily?
                        .Select(x => new SparklinePoint(x.Timestamp, x.Close))
                        .ToList() ?? []
                };
            })
            .ToList();
    }

    private static Application.Common.Exceptions.RequestValidationException DuplicateName() =>
        WatchlistRules.Invalid("name", "You already have a watchlist with this name.");
}
