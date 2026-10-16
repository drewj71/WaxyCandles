using WaxyCandles.Application.MarketData;
using WaxyCandles.Application.Options.Models;

namespace WaxyCandles.Application.Options.Gex;

/// <summary>
/// Builds the GEX/VEX table from a chain snapshot, under the usual naive dealer
/// model: customers buy puts and sell calls, so dealers are long call gamma
/// (positive) and short put gamma (negative). Real positioning varies; this is
/// the convention most GEX tools use.
/// </summary>
public static class GexCalculator
{
    private const int ContractMultiplier = 100;
    private const double DaysPerYear = 365.0;

    // Gamma diverges at expiry; a same-day contract counts as having at least
    // this long left so its node stays finite.
    private static readonly TimeSpan MinTimeToExpiry = TimeSpan.FromMinutes(30);

    public static GexTable Calculate(OptionChain chain, double riskFreeRate)
    {
        var spot = (double)chain.UnderlyingPrice;
        var cells = new Dictionary<(decimal Strike, DateOnly Expiration), (double Gex, double Vex)>();
        var contractCount = 0;
        var skippedCount = 0;

        foreach (var contract in chain.Contracts)
        {
            // No open interest, no exposure; expired contracts are already settled.
            if (contract.OpenInterest <= 0 || contract.Expiration <= chain.AsOf)
            {
                continue;
            }

            var timeLeft = contract.Expiration - chain.AsOf;
            var years = Math.Max(timeLeft.TotalDays, MinTimeToExpiry.TotalDays) / DaysPerYear;
            var strike = (double)contract.Strike;

            var volatility = contract.ImpliedVolatility is double iv and > 0
                ? iv
                : contract.Mid is decimal mid and > 0
                    ? BlackScholes.ImpliedVolatility(contract.Side, (double)mid, spot, strike, years, riskFreeRate)
                    : null;

            var gamma = contract.Gamma ?? (volatility is double vol
                ? BlackScholes.Gamma(spot, strike, years, vol, riskFreeRate)
                : null);

            if (gamma is not double g || !double.IsFinite(g))
            {
                skippedCount++;
                continue;
            }

            var vanna = volatility is double v
                ? BlackScholes.Vanna(spot, strike, years, v, riskFreeRate)
                : 0;

            if (!double.IsFinite(vanna))
            {
                vanna = 0;
            }

            var sign = contract.Side == OptionSide.Call ? 1 : -1;
            var shares = (double)contract.OpenInterest * ContractMultiplier;

            // Dollar delta per 1% move: gamma * shares * S * (S * 1%).
            var gex = sign * g * shares * spot * spot * 0.01;
            // Dollar delta per 1 vol point: vanna * shares * S * 0.01.
            var vex = sign * vanna * shares * spot * 0.01;

            var key = (contract.Strike, MarketHours.TradingDate(contract.Expiration));
            var current = cells.GetValueOrDefault(key);
            cells[key] = (current.Gex + gex, current.Vex + vex);
            contractCount++;
        }

        var expirations = cells.Keys
            .Select(x => x.Expiration)
            .Distinct()
            .Order()
            .ToList();

        var rows = cells.Keys
            .Select(x => x.Strike)
            .Distinct()
            .OrderDescending()
            .Select(strike =>
            {
                var gex = expirations
                    .Select(e => cells.TryGetValue((strike, e), out var c) ? c.Gex : (double?)null)
                    .ToList();
                var vex = expirations
                    .Select(e => cells.TryGetValue((strike, e), out var c) ? c.Vex : (double?)null)
                    .ToList();

                return new GexRow(strike, gex, vex, gex.Sum() ?? 0, vex.Sum() ?? 0);
            })
            .ToList();

        // Strike descending, then expiration, so ties resolve the same way every time.
        var ordered = cells
            .OrderByDescending(x => x.Key.Strike)
            .ThenBy(x => x.Key.Expiration)
            .ToList();

        return new GexTable(
            chain.Underlying,
            chain.UnderlyingPrice,
            chain.AsOf,
            expirations,
            rows,
            Levels(ordered.Select(x => new GexNode(x.Key.Strike, x.Key.Expiration, x.Value.Gex)), chain.UnderlyingPrice),
            Levels(ordered.Select(x => new GexNode(x.Key.Strike, x.Key.Expiration, x.Value.Vex)), chain.UnderlyingPrice),
            cells.Values.Sum(x => x.Gex),
            cells.Values.Sum(x => x.Vex),
            contractCount,
            skippedCount);
    }

    private static GexLevels Levels(IEnumerable<GexNode> cells, decimal spot)
    {
        var nodes = cells.Where(x => x.Value != 0).ToList();

        var king = nodes.MaxBy(x => Math.Abs(x.Value));

        GexNode? gatekeeper = null;

        if (king is not null)
        {
            var low = Math.Min(spot, king.Strike);
            var high = Math.Max(spot, king.Strike);

            gatekeeper = nodes
                .Where(x => x.Strike > low && x.Strike < high)
                .MaxBy(x => Math.Abs(x.Value));
        }

        return new GexLevels(
            king,
            nodes.Where(x => x.Strike < spot).MaxBy(x => Math.Abs(x.Value)),
            nodes.Where(x => x.Strike > spot).MaxBy(x => Math.Abs(x.Value)),
            gatekeeper);
    }
}
