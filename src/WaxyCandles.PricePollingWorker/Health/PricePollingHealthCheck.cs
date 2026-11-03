using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using WaxyCandles.Application.MarketData;
using WaxyCandles.Application.MarketData.Interfaces;
using WaxyCandles.Infrastructure.Health;
using WaxyCandles.Infrastructure.Telemetry;

namespace WaxyCandles.PricePollingWorker.Health;

/// <summary>"Health:PricePolling" configuration section. Optional.</summary>
public sealed class PricePollingHealthOptions
{
    public const string SectionName = "Health:PricePolling";

    /// <summary>
    /// How long the market may be open with no successful poll before the
    /// worker is unhealthy. Default: 3 × <c>PricePolling:Interval</c>, at least
    /// 5 minutes. Must be longer than the interval.
    /// </summary>
    public TimeSpan? MaxPollAge { get; set; }

    public TimeSpan EffectiveMaxPollAge(TimeSpan interval) =>
        MaxPollAge ?? TimeSpan.FromTicks(Math.Max(TimeSpan.FromMinutes(5).Ticks, interval.Ticks * 3));
}

/// <summary>
/// Unhealthy when the market is open and <see cref="PricePollingHeartbeat"/>
/// shows no successful poll for longer than the allowed age. While the market
/// is closed (by <see cref="MarketHours"/> and the same <see cref="IMarketCalendar"/>
/// the poller uses) it is healthy, since the poller deliberately sleeps then.
/// With <c>PricePolling:MarketHoursOnly</c> false the poller runs around the
/// clock, and so does this rule.
/// <para>
/// The age counts from the latest of: the last success, this check's creation
/// (process start), and today's session open (when polling only in market
/// hours), so start-up and each morning get the full allowance instead of
/// being measured against yesterday's last poll.
/// </para>
/// </summary>
public sealed class PricePollingHealthCheck : IHealthCheck
{
    public const string Name = "price-polling";

    private static readonly TimeZoneInfo MarketTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    // Must match MarketHours' regular-session open.
    private static readonly TimeSpan SessionOpen = new(9, 30, 0);

    private readonly IMarketCalendar _calendar;
    private readonly TimeProvider _timeProvider;
    private readonly Func<DateTimeOffset?> _lastSuccess;
    private readonly bool _marketHoursOnly;
    private readonly TimeSpan _maxPollAge;
    private readonly DateTimeOffset _startedAt;

    public PricePollingHealthCheck(
        IMarketCalendar calendar,
        TimeProvider timeProvider,
        IOptions<PricePollingOptions> pollingOptions,
        IOptions<PricePollingHealthOptions> healthOptions)
        : this(
            calendar,
            timeProvider,
            () => PricePollingHeartbeat.LastSuccessfulPollAt,
            pollingOptions.Value.MarketHoursOnly,
            healthOptions.Value.EffectiveMaxPollAge(pollingOptions.Value.Interval))
    {
    }

    public PricePollingHealthCheck(
        IMarketCalendar calendar,
        TimeProvider timeProvider,
        Func<DateTimeOffset?> lastSuccess,
        bool marketHoursOnly,
        TimeSpan maxPollAge)
    {
        _calendar = calendar;
        _timeProvider = timeProvider;
        _lastSuccess = lastSuccess;
        _marketHoursOnly = marketHoursOnly;
        _maxPollAge = maxPollAge;
        _startedAt = timeProvider.GetUtcNow();
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        if (_marketHoursOnly &&
            !await MarketHours.IsOpenAsync(now, _calendar, cancellationToken))
        {
            return HealthCheckResult.Healthy("Market closed; not polling.");
        }

        var lastSuccess = _lastSuccess();

        var since = Latest(
            lastSuccess ?? DateTimeOffset.MinValue,
            _startedAt,
            _marketHoursOnly ? SessionOpenOf(now) : DateTimeOffset.MinValue);

        var age = now - since;

        if (age <= _maxPollAge)
        {
            return HealthCheckResult.Healthy(lastSuccess is { } at
                ? $"Last successful poll at {at:u}."
                : "Waiting for the first poll.");
        }

        return new HealthCheckResult(
            context.Registration.FailureStatus,
            lastSuccess is { } last
                ? $"No successful price poll since {last:u} ({age:g}; limit {_maxPollAge:g})."
                : $"No successful price poll for {age:g} (limit {_maxPollAge:g}).");
    }

    private static DateTimeOffset Latest(params DateTimeOffset[] times) => times.Max();

    private static DateTimeOffset SessionOpenOf(DateTimeOffset now)
    {
        var date = MarketHours.TradingDate(now).ToDateTime(TimeOnly.FromTimeSpan(SessionOpen));
        return new DateTimeOffset(date, MarketTimeZone.GetUtcOffset(date));
    }
}

public static class PricePollingHealthExtensions
{
    public static IHealthChecksBuilder AddPricePollingCheck(
        this IHealthChecksBuilder builder,
        IConfiguration configuration)
    {
        builder.Services
            .AddOptions<PricePollingHealthOptions>()
            .Bind(configuration.GetSection(PricePollingHealthOptions.SectionName))
            .Validate<IOptions<PricePollingOptions>>(
                (health, polling) => health.MaxPollAge is null || health.MaxPollAge > polling.Value.Interval,
                "Health:PricePolling:MaxPollAge must be longer than PricePolling:Interval.")
            .ValidateOnStart();

        // A singleton, so the start-up allowance counts from process start.
        builder.Services.AddSingleton(sp => new PricePollingHealthCheck(
            sp.GetRequiredService<IMarketCalendar>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IOptions<PricePollingOptions>>(),
            sp.GetRequiredService<IOptions<PricePollingHealthOptions>>()));

        return builder.AddCheck<PricePollingHealthCheck>(PricePollingHealthCheck.Name);
    }
}
