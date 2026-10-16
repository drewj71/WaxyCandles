using WaxyCandles.Application.MarketData.Interfaces;
using WaxyCandles.Application.MarketData.Models;

namespace WaxyCandles.Application.MarketData;

/// <summary>
/// US equity regular trading hours: 09:30–16:00 America/New_York. Which dates
/// trade comes from an <see cref="IMarketCalendar"/>; the overloads without one
/// assume every weekday does. Early closes (13:00 half days) are not modelled;
/// polling after them only re-reads an unchanged price, which the price writer
/// skips.
/// </summary>
public static class MarketHours
{
    private static readonly TimeZoneInfo MarketTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static readonly TimeSpan Open = new(9, 30, 0);
    private static readonly TimeSpan Close = new(16, 0, 0);

    // A calendar that says "closed" for longer than any real market closure
    // is broken; stop looking and fall back to the weekday rule.
    private const int MaxClosedDaysInARow = 14;

    /// <summary>The US/Eastern date at this instant.</summary>
    public static DateOnly TradingDate(DateTimeOffset now) =>
        DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(now, MarketTimeZone).Date);

    public static bool IsWeekday(DateOnly date) =>
        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    /// <summary>Within the regular session of a weekday.</summary>
    public static bool IsOpen(DateTimeOffset now)
    {
        var eastern = TimeZoneInfo.ConvertTime(now, MarketTimeZone);

        return IsWeekday(DateOnly.FromDateTime(eastern.Date))
            && eastern.TimeOfDay >= Open
            && eastern.TimeOfDay < Close;
    }

    /// <summary>Within the regular session of a day the calendar says trades.</summary>
    public static async Task<bool> IsOpenAsync(
        DateTimeOffset now,
        IMarketCalendar calendar,
        CancellationToken cancellationToken = default) =>
        IsOpen(now) &&
        await calendar.IsTradingDayAsync(
            TradingDate(now),
            cancellationToken);

    /// <summary>The next weekday regular-session open strictly after <paramref name="now"/>.</summary>
    public static DateTimeOffset NextOpen(DateTimeOffset now)
    {
        var eastern = TimeZoneInfo.ConvertTime(now, MarketTimeZone);

        var date = DateOnly.FromDateTime(eastern.TimeOfDay < Open
            ? eastern.Date
            : eastern.Date.AddDays(1));

        while (!IsWeekday(date))
        {
            date = date.AddDays(1);
        }

        return SessionOpen(date);
    }

    /// <summary>
    /// The next regular-session open strictly after <paramref name="now"/> on a
    /// day the calendar says trades.
    /// </summary>
    public static async Task<DateTimeOffset> NextOpenAsync(
        DateTimeOffset now,
        IMarketCalendar calendar,
        CancellationToken cancellationToken = default)
    {
        var first = NextOpen(now);
        var open = first;

        for (var i = 0; i < MaxClosedDaysInARow; i++)
        {
            if (await calendar.IsTradingDayAsync(
                    TradingDate(open),
                    cancellationToken))
            {
                return open;
            }

            open = NextOpen(open);
        }

        return first;
    }

    /// <summary>
    /// Whether the regular session is open now, and when it next changes:
    /// today's close if open, else the next open on a day the calendar says trades.
    /// </summary>
    public static async Task<MarketStatus> StatusAsync(
        DateTimeOffset now,
        IMarketCalendar calendar,
        CancellationToken cancellationToken = default)
    {
        if (await IsOpenAsync(now, calendar, cancellationToken))
        {
            return new MarketStatus(true, SessionTime(TradingDate(now), Close));
        }

        return new MarketStatus(false, await NextOpenAsync(now, calendar, cancellationToken));
    }

    private static DateTimeOffset SessionOpen(DateOnly date) => SessionTime(date, Open);

    private static DateTimeOffset SessionTime(DateOnly date, TimeSpan timeOfDay)
    {
        var time = date.ToDateTime(TimeOnly.FromTimeSpan(timeOfDay));

        // The offset of that date, not today's, so a DST switch in between is honoured.
        return new DateTimeOffset(time, MarketTimeZone.GetUtcOffset(time));
    }
}
