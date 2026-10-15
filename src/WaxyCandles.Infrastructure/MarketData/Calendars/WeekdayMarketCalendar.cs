using WaxyCandles.Application.MarketData;
using WaxyCandles.Application.MarketData.Interfaces;

namespace WaxyCandles.Infrastructure.MarketData.Calendars;

/// <summary>Every weekday trades. For providers with no market calendar.</summary>
public class WeekdayMarketCalendar : IMarketCalendar
{
    public Task<bool> IsTradingDayAsync(
        DateOnly date,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(MarketHours.IsWeekday(date));
}
