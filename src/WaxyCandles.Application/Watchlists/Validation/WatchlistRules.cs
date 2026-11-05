using WaxyCandles.Application.Common.Exceptions;
using WaxyCandles.Application.MarketData;
using WaxyCandles.Domain.Entities;

namespace WaxyCandles.Application.Watchlists.Validation;

/// <summary>Limits and validation for watchlists. Each violation is a 400.</summary>
public static class WatchlistRules
{
    public const int MaxListsPerUser = 20;

    public const int MaxSymbolsPerList = 50;

    /// <summary>What the single-list endpoints (/api/watchlist) create on first use.</summary>
    public const string DefaultListName = "My Watchlist";

    /// <summary>The trimmed name; throws if it is empty or too long.</summary>
    public static string ValidateName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();

        if (trimmed.Length is 0 or > Watchlist.NameMaxLength)
        {
            throw Invalid("name", $"Name must be 1-{Watchlist.NameMaxLength} characters.");
        }

        return trimmed;
    }

    /// <summary>The normalized symbol; throws if it isn't valid.</summary>
    public static string ValidateSymbol(string? symbol)
    {
        if (!StockSymbol.TryNormalize(symbol, out var normalized))
        {
            throw Invalid("symbol", StockSymbol.InvalidMessage);
        }

        return normalized;
    }

    public static RequestValidationException Invalid(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });
}
