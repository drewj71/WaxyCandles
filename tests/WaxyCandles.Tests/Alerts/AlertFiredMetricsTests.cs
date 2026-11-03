using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure.Alerts;
using WaxyCandles.Tests.Infrastructure;
using WaxyCandles.Tests.Telemetry;
using static WaxyCandles.Infrastructure.Telemetry.WaxyCandlesMetrics;

namespace WaxyCandles.Tests.Alerts;

/// <summary>The waxycandles.alerts.fired counter, against real SQL Server.</summary>
public class AlertFiredMetricsTests(IntegrationFixture fixture)
    : DatabaseTestBase(fixture)
{
    private const string Fired = "waxycandles.alerts.fired";

    private readonly MetricRecorder _metrics = new();

    public override Task DisposeAsync()
    {
        _metrics.Dispose();
        return base.DisposeAsync();
    }

    [Fact]
    public async Task Each_fire_is_counted_once_by_kind()
    {
        await SeedAlertAsync(AlertDirection.Above, 100m);
        await SeedAlertAsync(AlertDirection.Above, 100m, level: GexLevelKind.King);
        await SeedAlertAsync(AlertDirection.Below, 100m); // not met

        var now = DateTimeOffset.UtcNow;
        await EvaluateAsync(150m, now);

        Assert.Equal(1, _metrics.Sum(Fired, (KindTag, "price")));
        Assert.Equal(1, _metrics.Sum(Fired, (KindTag, "level")));

        // Already triggered (Once): a later price fires nothing more.
        await EvaluateAsync(151m, now.AddSeconds(1));

        Assert.Equal(2, _metrics.Sum(Fired));
        Assert.All(_metrics.Measurements(Fired), m => Assert.Equal([KindTag], m.Tags.Keys));
    }

    private async Task EvaluateAsync(decimal price, DateTimeOffset at)
    {
        await using var db = NewDbContext();
        await new AlertEvaluationService(db).EvaluateAsync("AAPL", price, at);
    }

    private async Task SeedAlertAsync(
        AlertDirection direction,
        decimal target,
        GexLevelKind? level = null)
    {
        await using var db = NewDbContext();
        db.Alerts.Add(new Alert
        {
            Id = Guid.NewGuid(),
            UserId = "user-1",
            Symbol = "AAPL",
            Direction = direction,
            TargetPrice = target,
            Level = level,
            LevelPrice = level is null ? null : 101m,
            LevelProximityPercent = level is null ? null : Alert.DefaultLevelProximityPercent,
            LevelDays = level is null ? null : Alert.DefaultLevelDays,
            LevelStrikeRange = level is null ? null : Alert.DefaultLevelStrikeRange
        });
        await db.SaveChangesAsync();
    }
}
