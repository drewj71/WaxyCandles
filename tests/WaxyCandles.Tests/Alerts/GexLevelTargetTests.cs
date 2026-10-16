using WaxyCandles.Application.Alerts.Levels;
using WaxyCandles.Application.Options.Gex;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;

namespace WaxyCandles.Tests.Alerts;

public class GexLevelTargetTests
{
    [Fact]
    public void Price_below_the_level_fires_on_rising_to_within_the_proximity()
    {
        var (target, direction) = GexLevelTarget.For(levelPrice: 200m, currentPrice: 190m, proximityPercent: 0.5m);

        Assert.Equal(199m, target);
        Assert.Equal(AlertDirection.Above, direction);
    }

    [Fact]
    public void Price_above_the_level_fires_on_falling_to_within_the_proximity()
    {
        var (target, direction) = GexLevelTarget.For(levelPrice: 200m, currentPrice: 210m, proximityPercent: 0.5m);

        Assert.Equal(201m, target);
        Assert.Equal(AlertDirection.Below, direction);
    }

    [Fact]
    public void Zero_proximity_means_touching_the_level()
    {
        Assert.Equal((585m, AlertDirection.Above), GexLevelTarget.For(585m, 580m, 0m));
    }

    [Fact]
    public void Price_already_within_range_meets_the_condition_straight_away()
    {
        // 199.5 is within 0.5% (199-201) of 200, below it.
        var (target, direction) = GexLevelTarget.For(200m, 199.5m, 0.5m);

        var alert = new Alert { TargetPrice = target, Direction = direction };
        Assert.True(alert.IsConditionMet(199.5m));
    }

    [Fact]
    public void Target_is_rounded_to_the_stored_six_decimals()
    {
        var (target, _) = GexLevelTarget.For(587.5m, 580m, 0.333m);

        Assert.Equal(Math.Round(target, 6), target);
        Assert.Equal(585.543625m, target);
    }

    [Theory]
    [InlineData(GexLevelKind.King, 1)]
    [InlineData(GexLevelKind.Gatekeeper, 2)]
    [InlineData(GexLevelKind.Floor, 3)]
    [InlineData(GexLevelKind.Ceiling, 4)]
    public void Pick_returns_the_named_node(GexLevelKind kind, int strike)
    {
        var expiration = new DateOnly(2026, 10, 16);
        var levels = new GexLevels(
            new GexNode(1, expiration, 1),
            Floor: new GexNode(3, expiration, 3),
            Ceiling: new GexNode(4, expiration, 4),
            Gatekeeper: new GexNode(2, expiration, 2));

        Assert.Equal(strike, GexLevelTarget.Pick(levels, kind)!.Strike);
    }
}
