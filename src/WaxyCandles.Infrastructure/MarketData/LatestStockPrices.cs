using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Infrastructure.Persistence;

namespace WaxyCandles.Infrastructure.MarketData;

public static class LatestStockPrices
{
    /// <summary>
    /// The newest stored price of each symbol (symbols with none are left out),
    /// as one query that seeks the (Symbol, Timestamp) index once per symbol.
    /// A LINQ GroupBy-then-First becomes ROW_NUMBER over every stored price of
    /// those symbols instead, which is far more reading for a query the live
    /// price loop runs every few seconds. Composable, and untracked.
    /// </summary>
    /// <param name="symbols">Normalized symbols, as stored.</param>
    public static IQueryable<StockPrice> For(
        AppDbContext dbContext,
        IReadOnlyCollection<string> symbols)
    {
        var json = JsonSerializer.Serialize(symbols.Distinct(StringComparer.OrdinalIgnoreCase));

        // OPENJSON's column type matches StockPrices.Symbol (nvarchar(450)),
        // so the comparison seeks rather than converting each row.
        return dbContext.StockPrices
            .FromSql($"""
                SELECT p.[Id], p.[Symbol], p.[Price], p.[Change], p.[ChangePercent], p.[Timestamp]
                FROM OPENJSON({json}) WITH ([Symbol] nvarchar(450) '$') AS s
                CROSS APPLY (
                    SELECT TOP (1) sp.*
                    FROM [StockPrices] AS sp
                    WHERE sp.[Symbol] = s.[Symbol]
                    ORDER BY sp.[Timestamp] DESC
                ) AS p
                """)
            .AsNoTracking();
    }
}
