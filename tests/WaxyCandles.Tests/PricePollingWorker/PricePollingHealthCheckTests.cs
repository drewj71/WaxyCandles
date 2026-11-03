using Microsoft.Extensions.Diagnostics.HealthChecks;
using WaxyCandles.Application.MarketData;
using WaxyCandles.Application.MarketData.Interfaces;
using WaxyCandles.PricePollingWorker.Health;

namespace WaxyCandles.Tests.PricePollingWorker;

/// <summary>
/// The poller's liveness rule: stale polls only count while the market is open.
/// September 2026 is EDT, so the session is 13:30–20:00 UTC; 2026-09-02 is a
/// Wednesday and 2026-09-07 is Labor Day.
/// </summary>
public class PricePollingHealthCheckTests
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    private static readonly DateTimeOffset LongAgo = DateTimeOffset.Parse("2026-08-01T15:00:00Z");

    [Theory]
    [InlineData("2026-09-02T12:00:00Z")] // Wednesday, before the open
    [InlineData("2026-09-02T21:00:00Z")] // Wednesday, after the close
    [InlineData("2026-09-05T16:00:00Z")] // Saturday, mid-day
    [InlineData("2026-09-07T16:00:00Z")] // Labor Day, per the calendar
    public async Task Stale_heartbeat_is_healthy_while_the_market_is_closed(string now)
    {
        var result = await CheckAsync(now, lastSuccess: LongAgo, startedAt: LongAgo);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("Market closed", result.Description);
    }

    [Fact]
    public async Task Recent_poll_is_healthy_while_open()
    {
        var result = await CheckAsync(
            "2026-09-02T16:00:00Z",
            lastSuccess: DateTimeOffset.Parse("2026-09-02T15:57:00Z"),
            startedAt: LongAgo);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Stale_poll_is_unhealthy_while_open()
    {
        var result = await CheckAsync(
            "2026-09-02T16:00:00Z",
            lastSuccess: DateTimeOffset.Parse("2026-09-02T15:54:00Z"),
            startedAt: LongAgo);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("2026-09-02 15:54:00Z", result.Description);
    }

    [Fact]
    public async Task Each_session_gets_the_full_allowance_from_the_open()
    {
        // Last poll was yesterday's close. Not stale just after today's open...
        var yesterdaysLastPoll = DateTimeOffset.Parse("2026-09-01T19:59:00Z");

        var justOpened = await CheckAsync("2026-09-02T13:33:00Z", yesterdaysLastPoll, LongAgo);
        Assert.Equal(HealthStatus.Healthy, justOpened.Status);

        // ...but stale once the allowance since the open has passed.
        var later = await CheckAsync("2026-09-02T13:36:00Z", yesterdaysLastPoll, LongAgo);
        Assert.Equal(HealthStatus.Unhealthy, later.Status);
    }

    [Fact]
    public async Task Start_up_gets_the_full_allowance_before_the_first_poll()
    {
        var time = new MutableTime(DateTimeOffset.Parse("2026-09-02T16:00:00Z"));
        var check = new PricePollingHealthCheck(new WeekdayCalendar(), time, () => null, true, MaxAge);

        Assert.Equal(HealthStatus.Healthy, (await RunAsync(check)).Status);

        time.Now += TimeSpan.FromMinutes(6);

        var result = await RunAsync(check);
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("No successful price poll for", result.Description);
    }

    [Fact]
    public async Task Polling_around_the_clock_is_checked_around_the_clock()
    {
        var time = new MutableTime(LongAgo);

        var check = new PricePollingHealthCheck(
            new WeekdayCalendar(),
            time,
            () => DateTimeOffset.Parse("2026-09-05T02:50:00Z"),
            marketHoursOnly: false,
            MaxAge);

        time.Now = DateTimeOffset.Parse("2026-09-05T03:00:00Z"); // Saturday night

        Assert.Equal(HealthStatus.Unhealthy, (await RunAsync(check)).Status);
    }

    [Theory]
    [InlineData(null, "00:01:00", "00:05:00")] // floor of 5 minutes
    [InlineData(null, "00:15:00", "00:45:00")] // 3 × a long interval
    [InlineData("00:02:00", "00:01:00", "00:02:00")] // explicit
    public void Max_poll_age_defaults_from_the_interval(string? configured, string interval, string expected)
    {
        var options = new PricePollingHealthOptions
        {
            MaxPollAge = configured is null ? null : TimeSpan.Parse(configured)
        };

        Assert.Equal(TimeSpan.Parse(expected), options.EffectiveMaxPollAge(TimeSpan.Parse(interval)));
    }

    private static Task<HealthCheckResult> CheckAsync(
        string now,
        DateTimeOffset? lastSuccess,
        DateTimeOffset startedAt)
    {
        var time = new MutableTime(startedAt);
        var check = new PricePollingHealthCheck(new WeekdayCalendar(), time, () => lastSuccess, true, MaxAge);
        time.Now = DateTimeOffset.Parse(now);

        return RunAsync(check);
    }

    private static Task<HealthCheckResult> RunAsync(IHealthCheck check) =>
        check.CheckHealthAsync(new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(PricePollingHealthCheck.Name, check, null, null)
        });

    private sealed class MutableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class WeekdayCalendar : IMarketCalendar
    {
        private static readonly DateOnly LaborDay = new(2026, 9, 7);

        public Task<bool> IsTradingDayAsync(DateOnly date, CancellationToken cancellationToken = default) =>
            Task.FromResult(MarketHours.IsWeekday(date) && date != LaborDay);
    }
}
