using System.Text.RegularExpressions;
using WaxyCandles.Application.Common.Exceptions;

namespace WaxyCandles.Application.Options.Gex;

public static partial class GexRequestValidator
{
    // Letters first, then letters, digits, '.' or '-' (BRK.B, BF-B).
    [GeneratedRegex("^[A-Z][A-Z0-9.-]{0,9}$")]
    private static partial Regex SymbolPattern();

    /// <summary>The symbol trimmed and upper-cased; throws <see cref="RequestValidationException"/> if anything is invalid.</summary>
    public static string Validate(string symbol, GexRequest request)
    {
        var normalizedSymbol = symbol.Trim().ToUpperInvariant();
        var errors = new Dictionary<string, string[]>();

        if (!SymbolPattern().IsMatch(normalizedSymbol))
        {
            errors["symbol"] = ["Enter a ticker symbol such as AAPL."];
        }

        if (request.Days is < 0 or > GexRequest.MaxDays)
        {
            errors["days"] = [$"Days must be between 0 and {GexRequest.MaxDays}."];
        }

        if (request.StrikeRangePercent is <= 0 or > GexRequest.MaxStrikeRangePercent)
        {
            errors["strikeRange"] = [$"Strike range must be more than 0 and at most {GexRequest.MaxStrikeRangePercent}%."];
        }

        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors);
        }

        return normalizedSymbol;
    }
}
