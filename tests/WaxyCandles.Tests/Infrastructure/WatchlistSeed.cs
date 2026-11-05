using Microsoft.EntityFrameworkCore;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Infrastructure.Persistence;

namespace WaxyCandles.Tests.Infrastructure;

/// <summary>Puts symbols in a user's watchlist directly, bypassing the service.</summary>
public static class WatchlistSeed
{
    public const string DefaultName = "My Watchlist";

    /// <summary>Adds the symbol to the user's list called <paramref name="listName"/>, creating it if needed.</summary>
    public static async Task<Guid> AddAsync(
        string connectionString,
        string userId,
        string symbol,
        string listName = DefaultName)
    {
        await using var db = IntegrationFixture.CreateDbContext(connectionString);

        var list = await db.Watchlists
            .Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.UserId == userId && x.Name == listName);

        if (list is null)
        {
            list = new Watchlist
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Name = listName,
                SortOrder = await db.Watchlists.CountAsync(x => x.UserId == userId)
            };

            db.Watchlists.Add(list);
        }

        db.WatchlistItems.Add(new WatchlistItem
        {
            Id = Guid.NewGuid(),
            WatchlistId = list.Id,
            Symbol = symbol,
            SortOrder = list.Items.Count
        });

        await db.SaveChangesAsync();

        return list.Id;
    }
}
