using WaxyCandles.Application.Backtesting;

namespace WaxyCandles.Tests.Backtesting;

/// <summary>When stored daily candles are enough to skip a billed import.</summary>
public class CandleCoverageTests
{
    // Monday 2024-06-03 to Friday 2024-06-28: 20 weekdays.
    private static readonly DateOnly From = new(2024, 6, 3);
    private static readonly DateOnly End = new(2024, 6, 28);

    [Fact]
    public void Nothing_stored_is_not_covered()
    {
        Assert.False(CandleCoverage.Covers(From, End, false, null, null, 0));
    }

    [Fact]
    public void Every_weekday_stored_is_covered()
    {
        Assert.True(CandleCoverage.Covers(From, End, false, From, End, 20));
    }

    [Fact]
    public void A_holiday_at_either_end_or_in_the_middle_is_allowed()
    {
        // Juneteenth (Wed 06-19) closed, and the range starting on a holiday Monday.
        Assert.True(CandleCoverage.Covers(From, End, false, From.AddDays(1), End, 18));
        // An old range may end one weekday early (a holiday on the last day).
        Assert.True(CandleCoverage.Covers(From, End, false, From, End.AddDays(-1), 19));
    }

    [Fact]
    public void A_range_starting_on_a_weekend_starts_at_the_next_weekday()
    {
        Assert.True(CandleCoverage.Covers(From.AddDays(-2), End, false, From, End, 20));
    }

    [Fact]
    public void Missing_history_at_the_start_is_not_covered()
    {
        Assert.False(CandleCoverage.Covers(From, End, false, From.AddDays(7), End, 15));
    }

    [Fact]
    public void A_range_reaching_the_present_needs_the_last_closed_session()
    {
        // exactEnd: End is the last closed session per the calendar, so one day short is stale.
        Assert.False(CandleCoverage.Covers(From, End, true, From, End.AddDays(-1), 19));
        Assert.True(CandleCoverage.Covers(From, End, true, From, End, 20));
    }

    [Fact]
    public void A_hole_in_the_middle_is_not_covered()
    {
        // 20 weekdays allow 20 / 25 + 2 = 2 missing; 4 missing is a hole.
        Assert.False(CandleCoverage.Covers(From, End, false, From, End, 16));
        Assert.True(CandleCoverage.Covers(From, End, false, From, End, 18));
    }

    [Fact]
    public void A_range_with_no_weekdays_needs_nothing()
    {
        var saturday = new DateOnly(2024, 6, 1);

        Assert.True(CandleCoverage.Covers(saturday, saturday.AddDays(1), false, null, null, 0));
    }
}
