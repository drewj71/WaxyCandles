using WaxyCandles.Application.MarketData;
using WaxyCandles.Application.MarketData.Interfaces;

namespace WaxyCandles.Tests.MarketData;

/// <summary>
/// Regular US trading hours. Inputs and expectations are absolute instants, so
/// these hold whatever time zone the machine is in.
/// </summary>
public class MarketHoursTests
{
    [Theory]
    // Wednesday 2026-09-02, EDT (UTC-4): open 13:30Z, close 20:00Z.
    [InlineData("2026-09-02T13:29:59Z", false)]
    [InlineData("2026-09-02T13:30:00Z", true)]
    [InlineData("2026-09-02T19:59:59Z", true)]
    [InlineData("2026-09-02T20:00:00Z", false)]
    // Wednesday 2026-12-02, EST (UTC-5): open 14:30Z.
    [InlineData("2026-12-02T14:00:00Z", false)]
    [InlineData("2026-12-02T14:30:00Z", true)]
    // Saturday and Sunday, mid-session times.
    [InlineData("2026-09-05T15:00:00Z", false)]
    [InlineData("2026-09-06T15:00:00Z", false)]
    // Friday 23:00 Eastern is Saturday in UTC: still closed.
    [InlineData("2026-09-05T03:00:00Z", false)]
    public void Is_open_only_during_the_weekday_regular_session(string utc, bool expected)
    {
        Assert.Equal(expected, MarketHours.IsOpen(DateTimeOffset.Parse(utc)));
    }

    [Theory]
    // Before the open: today's open.
    [InlineData("2026-09-02T12:00:00Z", "2026-09-02T13:30:00Z")]
    // After the close: tomorrow's open.
    [InlineData("2026-09-02T21:00:00Z", "2026-09-03T13:30:00Z")]
    // Exactly at the open: strictly after, so tomorrow's.
    [InlineData("2026-09-02T13:30:00Z", "2026-09-03T13:30:00Z")]
    // Friday after the close and over the weekend: Monday's open.
    [InlineData("2026-09-04T21:00:00Z", "2026-09-07T13:30:00Z")]
    [InlineData("2026-09-06T12:00:00Z", "2026-09-07T13:30:00Z")]
    // Across the November DST switch (1st): Friday EDT, Monday EST.
    [InlineData("2026-10-30T21:00:00Z", "2026-11-02T14:30:00Z")]
    // Across the March DST switch (8th): Friday EST, Monday EDT.
    [InlineData("2026-03-06T22:00:00Z", "2026-03-09T13:30:00Z")]
    public void Next_open_is_the_following_weekday_0930_eastern(string utc, string expectedUtc)
    {
        Assert.Equal(
            DateTimeOffset.Parse(expectedUtc),
            MarketHours.NextOpen(DateTimeOffset.Parse(utc)));
    }

    // Labor Day 2026 is Monday 2026-09-07; Thanksgiving is Thursday 2026-11-26.
    private static readonly FakeCalendar Holidays = new(
        new DateOnly(2026, 9, 7),
        new DateOnly(2026, 11, 26));

    [Fact]
    public async Task Is_closed_mid_session_on_a_holiday()
    {
        Assert.False(await MarketHours.IsOpenAsync(
            DateTimeOffset.Parse("2026-09-07T15:00:00Z"), Holidays));
        Assert.True(await MarketHours.IsOpenAsync(
            DateTimeOffset.Parse("2026-09-08T15:00:00Z"), Holidays));
    }

    [Theory]
    // Friday after the close, over the weekend and Labor Day: Tuesday's open.
    [InlineData("2026-09-04T21:00:00Z", "2026-09-08T13:30:00Z")]
    // On the holiday itself.
    [InlineData("2026-09-07T15:00:00Z", "2026-09-08T13:30:00Z")]
    // Wednesday before Thanksgiving: Friday's open (EST).
    [InlineData("2026-11-25T22:00:00Z", "2026-11-27T14:30:00Z")]
    // An ordinary weeknight is unaffected.
    [InlineData("2026-09-02T21:00:00Z", "2026-09-03T13:30:00Z")]
    public async Task Next_open_skips_holidays(string utc, string expectedUtc)
    {
        Assert.Equal(
            DateTimeOffset.Parse(expectedUtc),
            await MarketHours.NextOpenAsync(DateTimeOffset.Parse(utc), Holidays));
    }

    [Fact]
    public async Task Calendar_that_never_opens_falls_back_to_the_next_weekday_open()
    {
        var neverOpen = new FakeCalendar(alwaysClosed: true);

        Assert.Equal(
            DateTimeOffset.Parse("2026-09-03T13:30:00Z"),
            await MarketHours.NextOpenAsync(DateTimeOffset.Parse("2026-09-02T21:00:00Z"), neverOpen));
    }

    private sealed class FakeCalendar(params DateOnly[] holidays) : IMarketCalendar
    {
        private readonly bool _alwaysClosed;

        public FakeCalendar(bool alwaysClosed) : this() => _alwaysClosed = alwaysClosed;

        public Task<bool> IsTradingDayAsync(DateOnly date, CancellationToken cancellationToken = default) =>
            Task.FromResult(!_alwaysClosed && MarketHours.IsWeekday(date) && !holidays.Contains(date));
    }
}
