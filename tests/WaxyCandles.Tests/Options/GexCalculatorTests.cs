using WaxyCandles.Application.Options.Gex;
using WaxyCandles.Application.Options.Models;

namespace WaxyCandles.Tests.Options;

public class GexCalculatorTests
{
    private const double Rate = 0.04;

    // Thursday 2026-10-15, 16:00 EDT.
    private static readonly DateTimeOffset AsOf = DateTimeOffset.Parse("2026-10-15T20:00:00Z");

    // Equity options expire 16:00 Eastern.
    private static DateTimeOffset Expiry(string date) =>
        DateTimeOffset.Parse($"{date}T20:00:00Z");

    private static readonly DateOnly Oct16 = new(2026, 10, 16);
    private static readonly DateOnly Oct23 = new(2026, 10, 23);

    private static OptionContract Contract(
        OptionSide side,
        decimal strike,
        string expiration = "2026-10-16",
        long openInterest = 1000,
        double? gamma = 0.05,
        double? iv = 0.3,
        decimal? mid = null) =>
        new($"X{strike}{side}", side, strike, Expiry(expiration), openInterest, iv, gamma, mid);

    private static GexTable Calculate(decimal spot, params OptionContract[] contracts) =>
        GexCalculator.Calculate(new OptionChain("XYZ", spot, AsOf, contracts), Rate);

    [Fact]
    public void Call_gamma_is_positive_and_put_gamma_negative_in_dollars_per_1_percent()
    {
        var table = Calculate(
            100m,
            Contract(OptionSide.Call, 105m),
            Contract(OptionSide.Put, 95m));

        // 0.05 gamma * 100,000 shares * $100 * $1 (1% of $100).
        Assert.Equal(500_000, table.Rows.Single(r => r.Strike == 105m).Gex[0]!.Value, 6);
        Assert.Equal(-500_000, table.Rows.Single(r => r.Strike == 95m).Gex[0]!.Value, 6);
        Assert.Equal(0, table.TotalGex, 6);
    }

    [Fact]
    public void Calls_and_puts_at_the_same_strike_and_expiration_net_in_one_cell()
    {
        var table = Calculate(
            100m,
            Contract(OptionSide.Call, 100m, openInterest: 3000),
            Contract(OptionSide.Put, 100m, openInterest: 1000));

        var row = Assert.Single(table.Rows);
        Assert.Equal(1_000_000, row.Gex[0]!.Value, 6);
        Assert.Equal(2, table.ContractCount);
    }

    [Fact]
    public void Rows_run_highest_strike_first_and_columns_earliest_expiration_first()
    {
        var table = Calculate(
            100m,
            Contract(OptionSide.Call, 95m, "2026-10-23"),
            Contract(OptionSide.Call, 105m, "2026-10-16"),
            Contract(OptionSide.Call, 100m, "2026-10-23"));

        Assert.Equal([105m, 100m, 95m], table.Rows.Select(r => r.Strike));
        Assert.Equal([Oct16, Oct23], table.Expirations);
        // An empty cell is null, not zero.
        Assert.Null(table.Rows[0].Gex[1]);
        Assert.Null(table.Rows[1].Gex[0]);
        Assert.Equal(table.Rows[1].Gex[1], table.Rows[1].NetGex);
    }

    [Fact]
    public void Contracts_without_open_interest_or_already_expired_are_ignored()
    {
        var table = Calculate(
            100m,
            Contract(OptionSide.Call, 100m, openInterest: 0),
            Contract(OptionSide.Call, 100m, "2026-10-15"));

        Assert.Empty(table.Rows);
        Assert.Equal(0, table.ContractCount);
        Assert.Equal(0, table.SkippedContractCount);
    }

    [Fact]
    public void Missing_gamma_is_computed_from_the_providers_iv()
    {
        var table = Calculate(100m, Contract(OptionSide.Call, 100m, "2026-10-23", gamma: null, iv: 0.3));

        var expectedGamma = BlackScholes.Gamma(100, 100, 8 / 365.0, 0.3, Rate);
        Assert.Equal(expectedGamma * 1000 * 100 * 100 * 1, table.Rows[0].Gex[0]!.Value, 3);
    }

    [Fact]
    public void Missing_gamma_and_iv_are_derived_from_the_mid_price()
    {
        var years = 8 / 365.0;
        var mid = BlackScholes.Price(OptionSide.Put, 100, 100, years, 0.45, Rate);

        var table = Calculate(
            100m,
            Contract(OptionSide.Put, 100m, "2026-10-23", gamma: null, iv: null, mid: (decimal)mid));

        var expectedGamma = BlackScholes.Gamma(100, 100, years, 0.45, Rate);
        Assert.Equal(-expectedGamma * 1000 * 100 * 100 * 1, table.Rows[0].Gex[0]!.Value, 0);
    }

    [Fact]
    public void Contracts_with_no_way_to_get_gamma_are_counted_as_skipped()
    {
        var table = Calculate(
            100m,
            Contract(OptionSide.Call, 100m, gamma: null, iv: null, mid: null),
            Contract(OptionSide.Call, 105m));

        Assert.Equal(1, table.ContractCount);
        Assert.Equal(1, table.SkippedContractCount);
        Assert.Equal([105m], table.Rows.Select(r => r.Strike));
    }

    [Fact]
    public void Vex_uses_black_scholes_vanna_with_the_same_dealer_signs()
    {
        var table = Calculate(100m, Contract(OptionSide.Put, 110m, "2026-10-23", iv: 0.3));

        var vanna = BlackScholes.Vanna(100, 110, 8 / 365.0, 0.3, Rate);
        // Vanna * 100,000 shares * $100 * 0.01, negated for a put.
        Assert.Equal(-vanna * 100_000 * 100 * 0.01, table.Rows[0].Vex[0]!.Value, 3);
    }

    [Fact]
    public void Levels_pick_king_floor_ceiling_and_gatekeeper_by_absolute_size()
    {
        var table = Calculate(
            100m,
            // King: the biggest node, a put wall below the price.
            Contract(OptionSide.Put, 90m, openInterest: 5000),
            // Between the price and the King.
            Contract(OptionSide.Call, 95m, openInterest: 2000),
            // Smaller floor candidate.
            Contract(OptionSide.Call, 97m, openInterest: 1000),
            // Ceiling.
            Contract(OptionSide.Call, 110m, openInterest: 3000));

        var levels = table.GexLevels;
        Assert.Equal(90m, levels.King!.Strike);
        Assert.True(levels.King.Value < 0);
        Assert.Equal(Oct16, levels.King.Expiration);
        Assert.Equal(90m, levels.Floor!.Strike);
        Assert.Equal(110m, levels.Ceiling!.Strike);
        Assert.Equal(95m, levels.Gatekeeper!.Strike);
    }

    [Fact]
    public void Empty_chain_has_no_levels()
    {
        var table = Calculate(100m);

        Assert.Empty(table.Rows);
        Assert.Null(table.GexLevels.King);
        Assert.Null(table.GexLevels.Gatekeeper);
        Assert.Null(table.VexLevels.Floor);
    }
}
