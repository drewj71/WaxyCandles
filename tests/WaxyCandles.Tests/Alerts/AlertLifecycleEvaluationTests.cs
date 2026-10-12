using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WaxyCandles.Application.Alerts.Messages;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure.Alerts;
using WaxyCandles.Infrastructure.Persistence;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Alerts;

/// <summary>
/// Enable/disable, expiry, and repeating (EveryCross) alerts with cooldown.
/// Time is simulated by moving TriggeredAt into the past in the database.
/// </summary>
public class AlertLifecycleEvaluationTests(IntegrationFixture fixture)
    : DatabaseTestBase(fixture)
{
    [Fact]
    public async Task Disabled_alert_is_not_triggered()
    {
        var alert = await SeedAsync(a => a.IsEnabled = false);

        await EvaluateAsync(150m);

        var saved = await LoadAsync(alert.Id);
        Assert.False(saved.IsTriggered);
        Assert.Equal(0, await EventCountAsync());
    }

    [Fact]
    public async Task Expired_alert_is_not_triggered()
    {
        var alert = await SeedAsync(a => a.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1));

        await EvaluateAsync(150m);

        Assert.False((await LoadAsync(alert.Id)).IsTriggered);
        Assert.Equal(0, await EventCountAsync());
    }

    [Fact]
    public async Task Alert_with_a_future_expiry_still_triggers()
    {
        var alert = await SeedAsync(a => a.ExpiresAt = DateTimeOffset.UtcNow.AddDays(1));

        await EvaluateAsync(150m);

        Assert.True((await LoadAsync(alert.Id)).IsTriggered);
        Assert.Equal(1, await EventCountAsync());
    }

    [Fact]
    public async Task One_time_alert_does_not_rearm_when_the_price_moves_back()
    {
        var alert = await SeedAsync();

        await EvaluateAsync(150m);
        await EvaluateAsync(90m);
        await EvaluateAsync(150m);

        var saved = await LoadAsync(alert.Id);
        Assert.True(saved.IsTriggered);
        Assert.Equal(1, saved.TriggerCount);
        Assert.Equal(1, await EventCountAsync());
    }

    [Fact]
    public async Task Repeating_alert_fires_once_while_the_price_stays_beyond_the_target()
    {
        var alert = await SeedAsync(a => a.Frequency = AlertFrequency.EveryCross);

        await EvaluateAsync(150m);
        await PassCooldownAsync(alert.Id);
        await EvaluateAsync(155m);

        // Still above the target: it hasn't crossed again, so no second fire
        // even though the cooldown has passed.
        var saved = await LoadAsync(alert.Id);
        Assert.True(saved.IsTriggered);
        Assert.Equal(1, saved.TriggerCount);
        Assert.Equal(1, await EventCountAsync());
    }

    [Fact]
    public async Task Repeating_alert_rearms_without_an_event_when_the_price_moves_back()
    {
        var alert = await SeedAsync(a => a.Frequency = AlertFrequency.EveryCross);

        await EvaluateAsync(150m);
        await EvaluateAsync(90m);

        var saved = await LoadAsync(alert.Id);
        Assert.False(saved.IsTriggered);
        Assert.Equal(AlertStatus.Active, saved.GetStatus(DateTimeOffset.UtcNow));
        Assert.Equal(1, saved.TriggerCount);
        Assert.Equal(1, await EventCountAsync());
    }

    [Fact]
    public async Task Repeating_alert_fires_again_on_the_next_cross_after_its_cooldown()
    {
        var alert = await SeedAsync(a => a.Frequency = AlertFrequency.EveryCross);

        await EvaluateAsync(150m);
        await EvaluateAsync(90m);
        await PassCooldownAsync(alert.Id);
        await EvaluateAsync(150m);

        var saved = await LoadAsync(alert.Id);
        Assert.True(saved.IsTriggered);
        Assert.Equal(2, saved.TriggerCount);

        var events = await EventsAsync();
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(alert.Id, e.AlertId));
        Assert.Equal(2, events.Select(e => e.MessageId).Distinct().Count());
    }

    [Fact]
    public async Task Cross_within_the_cooldown_waits_and_fires_once_it_has_passed()
    {
        var alert = await SeedAsync(a =>
        {
            a.Frequency = AlertFrequency.EveryCross;
            a.CooldownMinutes = 30;
        });

        await EvaluateAsync(150m);
        await EvaluateAsync(90m);
        await EvaluateAsync(150m);

        // Crossed again within 30 minutes: armed but not fired.
        var waiting = await LoadAsync(alert.Id);
        Assert.False(waiting.IsTriggered);
        Assert.Equal(1, waiting.TriggerCount);

        await PassCooldownAsync(alert.Id);
        await EvaluateAsync(150m);

        Assert.Equal(2, (await LoadAsync(alert.Id)).TriggerCount);
        Assert.Equal(2, await EventCountAsync());
    }

    [Fact]
    public async Task Re_armed_one_time_alert_fires_without_waiting_for_a_cooldown()
    {
        // E.g. the user edited a fired one-time alert, which re-arms it. Its last
        // fire was a minute ago, but the cooldown only applies to repeating alerts.
        var alert = await SeedAsync(a =>
        {
            a.TriggeredAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            a.TriggerCount = 1;
        });

        await EvaluateAsync(150m);

        var saved = await LoadAsync(alert.Id);
        Assert.True(saved.IsTriggered);
        Assert.Equal(2, saved.TriggerCount);
    }

    [Fact]
    public async Task Concurrent_evaluations_fire_a_rearmed_repeating_alert_once()
    {
        var alert = await SeedAsync(a =>
        {
            a.Frequency = AlertFrequency.EveryCross;
            a.TriggeredAt = DateTimeOffset.UtcNow.AddHours(-1);
            a.TriggerCount = 1;
        });

        var barrier = new AlertUpdateBarrier(parties: 2);

        await Task.WhenAll(
            EvaluateThroughAsync(barrier, 150m),
            EvaluateThroughAsync(barrier, 151m));

        Assert.Equal(2, barrier.Arrived);
        Assert.Equal(2, (await LoadAsync(alert.Id)).TriggerCount);
        Assert.Equal(1, await EventCountAsync());
    }

    private async Task EvaluateAsync(decimal price)
    {
        await using var db = NewDbContext();
        await new AlertEvaluationService(db).EvaluateAsync("AAPL", price);
    }

    private async Task EvaluateThroughAsync(AlertUpdateBarrier barrier, decimal price)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(ConnectionString)
            .AddInterceptors(barrier)
            .Options;

        await using var db = new AppDbContext(options);
        await new AlertEvaluationService(db).EvaluateAsync("AAPL", price);
    }

    /// <summary>Above 100, AAPL, enabled, one-time, unless configured otherwise.</summary>
    private async Task<Alert> SeedAsync(Action<Alert>? configure = null)
    {
        var alert = new Alert
        {
            Id = Guid.NewGuid(),
            UserId = "user-1",
            Symbol = "AAPL",
            Direction = AlertDirection.Above,
            TargetPrice = 100m
        };

        configure?.Invoke(alert);

        await using var db = NewDbContext();
        db.Alerts.Add(alert);
        await db.SaveChangesAsync();

        return alert;
    }

    private async Task PassCooldownAsync(Guid alertId)
    {
        await using var db = NewDbContext();
        var alert = await db.Alerts.SingleAsync(a => a.Id == alertId);
        alert.TriggeredAt = alert.TriggeredAt?.AddMinutes(-alert.CooldownMinutes - 1);
        await db.SaveChangesAsync();
    }

    private async Task<Alert> LoadAsync(Guid id)
    {
        await using var db = NewDbContext();
        return await db.Alerts.SingleAsync(a => a.Id == id);
    }

    private async Task<int> EventCountAsync() => (await EventsAsync()).Count;

    private async Task<List<AlertTriggeredMessage>> EventsAsync()
    {
        await using var db = NewDbContext();
        return (await db.OutboxMessages
                .Where(m => m.Type == nameof(AlertTriggeredMessage))
                .ToListAsync())
            .Select(m => JsonSerializer.Deserialize<AlertTriggeredMessage>(m.Payload)!)
            .ToList();
    }
}
