using WaxyCandles.PricePollingWorker;

namespace WaxyCandles.Tests.PricePolling;

/// <summary>When the polling worker refreshes GEX level alerts.</summary>
public class DailyRefreshScheduleTests
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(15);

    // Thursday 2026-10-15, 09:30 EDT.
    private static readonly DateTimeOffset Open = DateTimeOffset.Parse("2026-10-15T13:30:00Z");

    [Fact]
    public void Due_at_first_then_not_again_the_same_trading_day()
    {
        var schedule = new DailyRefreshSchedule(RetryDelay);

        Assert.True(schedule.IsDue(Open));
        schedule.Succeeded(Open);

        Assert.False(schedule.IsDue(Open.AddHours(6)));
    }

    [Fact]
    public void Due_again_on_the_next_eastern_date_not_the_next_utc_date()
    {
        var schedule = new DailyRefreshSchedule(RetryDelay);
        schedule.Succeeded(Open);

        // 21:00 EDT on the 15th is already the 16th in UTC.
        Assert.False(schedule.IsDue(DateTimeOffset.Parse("2026-10-16T01:00:00Z")));
        Assert.True(schedule.IsDue(Open.AddDays(1)));
    }

    [Fact]
    public void Failure_is_retried_after_the_delay_not_on_every_poll()
    {
        var schedule = new DailyRefreshSchedule(RetryDelay);

        Assert.Equal(Open + RetryDelay, schedule.Failed(Open));

        Assert.False(schedule.IsDue(Open.AddMinutes(1)));
        Assert.False(schedule.IsDue(Open.AddMinutes(14)));
        Assert.True(schedule.IsDue(Open.AddMinutes(15)));
    }

    [Fact]
    public void Success_after_a_retry_ends_the_day()
    {
        var schedule = new DailyRefreshSchedule(RetryDelay);
        schedule.Failed(Open);
        schedule.Succeeded(Open.AddMinutes(15));

        Assert.False(schedule.IsDue(Open.AddMinutes(30)));
        Assert.True(schedule.IsDue(Open.AddDays(1)));
    }
}
