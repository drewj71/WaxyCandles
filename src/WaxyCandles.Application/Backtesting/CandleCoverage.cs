using WaxyCandles.Application.MarketData;

namespace WaxyCandles.Application.Backtesting;

/// <summary>
/// Whether the stored daily candles cover a date range well enough to skip a
/// (billed) import. Uses the weekday rule with room for holidays, so it needs
/// no calendar requests for past dates:
/// <list type="bullet">
/// <item>the first stored candle is at most one weekday after the first
/// weekday of the range (a holiday);</item>
/// <item>the last stored candle is on the expected last date when it is known
/// exactly (<paramref name="exactEnd"/>: the last closed session, from the
/// market calendar), else at most one weekday before the last weekday;</item>
/// <item>no more than 4% + 2 of the range's weekdays are missing (US markets
/// close about 9-10 weekdays a year), which catches holes in the middle.</item>
/// </list>
/// A symbol that listed after <c>from</c> never counts as covered, so each
/// backtest of it asks the provider again (still at most once per request).
/// </summary>
public static class CandleCoverage
{
    public static bool Covers(
        DateOnly from,
        DateOnly end,
        bool exactEnd,
        DateOnly? firstStored,
        DateOnly? lastStored,
        int storedCount)
    {
        var weekdays = 0;
        DateOnly? firstWeekday = null;
        DateOnly? lastWeekday = null;

        for (var day = from; day <= end; day = day.AddDays(1))
        {
            if (!MarketHours.IsWeekday(day))
            {
                continue;
            }

            weekdays++;
            firstWeekday ??= day;
            lastWeekday = day;
        }

        if (firstWeekday is not { } expectedFirst || lastWeekday is not { } expectedLast)
        {
            // No weekday in the range: nothing to import.
            return true;
        }

        if (firstStored is not { } first || lastStored is not { } last)
        {
            return false;
        }

        if (first > NextWeekday(expectedFirst))
        {
            return false;
        }

        if (last < (exactEnd ? end : PreviousWeekday(expectedLast)))
        {
            return false;
        }

        var allowedMissing = weekdays / 25 + 2;

        return storedCount >= weekdays - allowedMissing;
    }

    private static DateOnly NextWeekday(DateOnly date)
    {
        do
        {
            date = date.AddDays(1);
        }
        while (!MarketHours.IsWeekday(date));

        return date;
    }

    private static DateOnly PreviousWeekday(DateOnly date)
    {
        do
        {
            date = date.AddDays(-1);
        }
        while (!MarketHours.IsWeekday(date));

        return date;
    }
}
