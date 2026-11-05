using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace WaxyCandles.Application.MarketData;

/// <summary>
/// How the API normalizes and validates a ticker symbol: alerts, watchlists
/// and live-price subscriptions all use these rules, so any symbol a user can
/// watch is one an alert accepts (and the price poller fetches).
/// </summary>
public static partial class StockSymbol
{
    public const int MaxLength = 10;

    public const string InvalidMessage =
        "Symbol must be 1-10 characters: letters, digits, '.' or '-'.";

    [GeneratedRegex("^[A-Z0-9.-]{1,10}$")]
    private static partial Regex Pattern();

    /// <summary>Trimmed and upper-cased, as symbols are stored.</summary>
    public static string Normalize(string? symbol) =>
        (symbol ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>Whether <paramref name="normalized"/> (already normalized) is a valid symbol.</summary>
    public static bool IsValid(string normalized) =>
        Pattern().IsMatch(normalized);

    /// <summary>Normalizes <paramref name="symbol"/>; false if the result isn't valid.</summary>
    public static bool TryNormalize(string? symbol, [NotNullWhen(true)] out string? normalized)
    {
        var candidate = Normalize(symbol);

        if (IsValid(candidate))
        {
            normalized = candidate;
            return true;
        }

        normalized = null;
        return false;
    }
}
