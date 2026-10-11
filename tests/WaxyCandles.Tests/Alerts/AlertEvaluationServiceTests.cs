using Microsoft.EntityFrameworkCore;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure.Alerts;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Alerts;

public class AlertEvaluationServiceTests(IntegrationFixture fixture)
    : DatabaseTestBase(fixture)
{
    public static TheoryData<AlertDirection, decimal, decimal, bool> DirectionCases => new()
    {
        // direction,           target,  price,   triggered
        { AlertDirection.Above, 100m,    150m,    true },
        { AlertDirection.Above, 100m,    100m,    true },  // boundary: >= triggers
        { AlertDirection.Above, 100m,    99.99m,  false },
        { AlertDirection.Below, 100m,    50m,     true },
        { AlertDirection.Below, 100m,    100m,    true },  // boundary: <= triggers
        { AlertDirection.Below, 100m,    100.01m, false },
    };

    [Theory]
    [MemberData(nameof(DirectionCases))]
    public async Task Alert_triggers_according_to_direction_and_target(
        AlertDirection direction,
        decimal targetPrice,
        decimal price,
        bool expectedTriggered)
    {
        var alert = await SeedAlertAsync("AAPL", direction, targetPrice);

        var before = DateTimeOffset.UtcNow;
        await EvaluateAsync("AAPL", price);
        var after = DateTimeOffset.UtcNow;

        var saved = await LoadAlertAsync(alert.Id);

        Assert.Equal(expectedTriggered, saved.IsTriggered);

        if (expectedTriggered)
        {
            Assert.NotNull(saved.TriggeredAt);
            Assert.InRange(saved.TriggeredAt!.Value, before, after);
        }
        else
        {
            Assert.Null(saved.TriggeredAt);
        }
    }

    [Fact]
    public async Task Already_triggered_alert_is_not_re_evaluated()
    {
        var originalTriggeredAt = DateTimeOffset.UtcNow.AddDays(-1);

        var alert = await SeedAlertAsync(
            "AAPL",
            AlertDirection.Above,
            100m,
            isTriggered: true,
            triggeredAt: originalTriggeredAt);

        await EvaluateAsync("AAPL", 500m);

        var saved = await LoadAlertAsync(alert.Id);

        Assert.True(saved.IsTriggered);
        Assert.Equal(originalTriggeredAt, saved.TriggeredAt);
    }

    [Fact]
    public async Task Only_alerts_for_the_evaluated_symbol_are_affected()
    {
        var aapl = await SeedAlertAsync("AAPL", AlertDirection.Above, 100m);
        var msft = await SeedAlertAsync("MSFT", AlertDirection.Above, 100m);

        await EvaluateAsync("AAPL", 150m);

        Assert.True((await LoadAlertAsync(aapl.Id)).IsTriggered);
        Assert.False((await LoadAlertAsync(msft.Id)).IsTriggered);
    }

    [Fact]
    public async Task Each_alert_on_a_symbol_is_evaluated_independently()
    {
        var above = await SeedAlertAsync("AAPL", AlertDirection.Above, 120m);
        var belowHit = await SeedAlertAsync("AAPL", AlertDirection.Below, 130m);
        var aboveMiss = await SeedAlertAsync("AAPL", AlertDirection.Above, 200m);
        var otherUser = await SeedAlertAsync("AAPL", AlertDirection.Above, 110m, userId: "user-2");

        await EvaluateAsync("AAPL", 125m);

        Assert.True((await LoadAlertAsync(above.Id)).IsTriggered);
        Assert.True((await LoadAlertAsync(belowHit.Id)).IsTriggered);
        Assert.False((await LoadAlertAsync(aboveMiss.Id)).IsTriggered);
        Assert.True((await LoadAlertAsync(otherUser.Id)).IsTriggered);
    }

    [Fact]
    public async Task Evaluating_a_symbol_with_no_alerts_is_a_no_op()
    {
        await EvaluateAsync("NONE", 1m);

        await using var db = NewDbContext();
        Assert.Equal(0, await db.Alerts.CountAsync());
    }

    private async Task EvaluateAsync(string symbol, decimal price)
    {
        await using var db = NewDbContext();
        await new AlertEvaluationService(db).EvaluateAsync(symbol, price);
    }

    private async Task<Alert> SeedAlertAsync(
        string symbol,
        AlertDirection direction,
        decimal targetPrice,
        string userId = "user-1",
        bool isTriggered = false,
        DateTimeOffset? triggeredAt = null)
    {
        var alert = new Alert
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Symbol = symbol,
            Direction = direction,
            TargetPrice = targetPrice,
            IsTriggered = isTriggered,
            TriggeredAt = triggeredAt
        };

        await using var db = NewDbContext();
        db.Alerts.Add(alert);
        await db.SaveChangesAsync();

        return alert;
    }

    private async Task<Alert> LoadAlertAsync(Guid id)
    {
        await using var db = NewDbContext();
        return await db.Alerts.SingleAsync(a => a.Id == id);
    }
}
