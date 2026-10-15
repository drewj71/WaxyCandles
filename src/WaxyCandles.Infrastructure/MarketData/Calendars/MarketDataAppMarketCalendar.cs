using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WaxyCandles.Application.MarketData;
using WaxyCandles.Application.MarketData.Interfaces;
using WaxyCandles.Infrastructure.MarketData.Providers;

namespace WaxyCandles.Infrastructure.MarketData.Calendars;

/// <summary>
/// Trading days from Market Data's markets/status endpoint, which reports
/// weekends and holidays as closed (half days as open). One request loads the
/// next <see cref="LookAhead"/> days; the result is kept for a day. Registered
/// as a singleton so the cache outlives each poll.
/// </summary>
public class MarketDataAppMarketCalendar : IMarketCalendar
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(1);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(15);
    private const int LookAhead = 30;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MarketDataAppMarketCalendar> _logger;

    private readonly SemaphoreSlim _lock = new(1, 1);

    // null: the API had no answer for that date (beyond its data), so the
    // weekday rule applies. Kept so the date isn't asked about again until expiry.
    private readonly Dictionary<DateOnly, bool?> _days = [];
    private DateTimeOffset _cacheExpiresAt = DateTimeOffset.MinValue;
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;

    public MarketDataAppMarketCalendar(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        TimeProvider timeProvider,
        ILogger<MarketDataAppMarketCalendar> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<bool> IsTradingDayAsync(
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        // Weekends never trade; no need to ask.
        if (!MarketHours.IsWeekday(date))
        {
            return false;
        }

        await _lock.WaitAsync(cancellationToken);

        try
        {
            var now = _timeProvider.GetUtcNow();

            if (now >= _cacheExpiresAt)
            {
                _days.Clear();
                _cacheExpiresAt = now + CacheLifetime;
            }

            if (!_days.ContainsKey(date) && now >= _retryAt)
            {
                await LoadAsync(date, now, cancellationToken);
            }

            return _days.GetValueOrDefault(date) ?? true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task LoadAsync(
        DateOnly from,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var to = from.AddDays(LookAhead - 1);

        try
        {
            using var document = await MarketDataAppApi.GetAsync(
                _httpClientFactory.CreateClient(),
                _configuration,
                $"{MarketDataAppApi.BaseUrl}/markets/status/" +
                $"?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}",
                cancellationToken);

            // Every date in the range gets an entry, answered or not, so an
            // unanswered date doesn't trigger another request on every poll.
            for (var day = from; day <= to; day = day.AddDays(1))
            {
                _days[day] = null;
            }

            if (document is null)
            {
                return;
            }

            var dates = document.RootElement.GetProperty("date");
            var statuses = document.RootElement.GetProperty("status");

            for (var i = 0; i < dates.GetArrayLength(); i++)
            {
                var day = MarketHours.TradingDate(
                    DateTimeOffset.FromUnixTimeSeconds(dates[i].GetInt64()));

                _days[day] = statuses[i].GetString() switch
                {
                    "open" => true,
                    "closed" => false,
                    _ => null
                };
            }
        }
        // Includes HttpClient timeouts (TaskCanceledException) but not shutdown.
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _retryAt = now + RetryAfterFailure;

            _logger.LogWarning(
                ex,
                "Could not load the market calendar; treating weekdays as trading days. Retrying after {RetryAt:u}.",
                _retryAt);
        }
    }
}
