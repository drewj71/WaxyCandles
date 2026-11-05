using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WaxyCandles.Application.Alerts.Interfaces;
using WaxyCandles.Application.MarketData.Interfaces;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Infrastructure.MarketData;
using WaxyCandles.Infrastructure.Telemetry;
using WaxyCandles.PricePollingWorker;
using WaxyCandles.Tests.Infrastructure;
using WaxyCandles.Tests.MarketData;
using static WaxyCandles.Infrastructure.Telemetry.WaxyCandlesMetrics;

namespace WaxyCandles.Tests.Telemetry;

/// <summary>
/// The real PricePollingWorker and MarketDataService against SQL Server, with a
/// fake quote provider: poll duration, prices stored and the last-success heartbeat.
/// </summary>
public class PricePollingMetricsTests(IntegrationFixture fixture)
    : DatabaseTestBase(fixture)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly RunningWorkers _workers = new();
    private readonly FakeMarketDataProvider _provider = new();
    private readonly MetricRecorder _metrics = new();

    public override async Task DisposeAsync()
    {
        await _workers.DisposeAsync();
        _metrics.Dispose();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Successful_poll_records_its_duration_the_prices_stored_and_the_heartbeat()
    {
        await SeedWatchlistAsync("AAPL");
        await SeedWatchlistAsync("MSFT");
        _provider.SetQuote("AAPL", 150m, DateTimeOffset.UtcNow.AddSeconds(-5));
        _provider.SetQuote("MSFT", 300m, DateTimeOffset.UtcNow.AddSeconds(-5));

        var before = DateTimeOffset.UtcNow;
        await StartWorkerAsync();

        await Eventually.TrueAsync(
            () => PricePollingHeartbeat.LastSuccessfulPollAt >= before,
            Timeout,
            "a successful poll");

        await Eventually.TrueAsync(
            () => _metrics.Measurements("waxycandles.price_poll.duration", (OutcomeTag, "success")).Count >= 1,
            Timeout,
            "the poll duration to be recorded");

        Assert.Equal(2, _metrics.Sum("waxycandles.prices.stored"));
        Assert.Empty(_metrics.Measurements("waxycandles.price_poll.duration", (OutcomeTag, "failure")));

        var lastSuccess = _metrics.Gauge("waxycandles.price_poll.last_success");
        Assert.NotNull(lastSuccess);
        Assert.InRange(
            (long)lastSuccess.Value,
            before.ToUnixTimeSeconds(),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task Failed_poll_records_a_failure_and_leaves_the_heartbeat_alone()
    {
        await SeedWatchlistAsync("AAPL");
        _provider.FailingSymbols.Add("AAPL");

        var before = DateTimeOffset.UtcNow;
        await StartWorkerAsync();

        await Eventually.TrueAsync(
            () => _metrics.Measurements("waxycandles.price_poll.duration", (OutcomeTag, "failure")).Count >= 1,
            Timeout,
            "the failed poll to be recorded");

        Assert.False(PricePollingHeartbeat.LastSuccessfulPollAt >= before);
        Assert.Equal(0, _metrics.Sum("waxycandles.prices.stored"));
    }

    private async Task StartWorkerAsync()
    {
        var services = Fixture.BuildServices(
            ConnectionString,
            configure: s =>
            {
                s.RemoveAll<IMarketDataProvider>();
                s.AddSingleton<IMarketDataProvider>(_provider);
                s.AddScoped<IGexLevelAlertRefresher, NoLevelAlerts>();
                s.Configure<PricePollingOptions>(o =>
                {
                    o.MarketHoursOnly = false;
                    o.Interval = TimeSpan.FromSeconds(5);
                });
            });

        await _workers.StartAsync<WaxyCandles.PricePollingWorker.PricePollingWorker>(services);
    }

    private Task SeedWatchlistAsync(string symbol) =>
        WatchlistSeed.AddAsync(ConnectionString, "user-1", symbol);

    private sealed class NoLevelAlerts : IGexLevelAlertRefresher
    {
        public Task<bool> RefreshAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
