using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using WaxyCandles.Application.Alerts.DTOs;
using WaxyCandles.Application.Alerts.Messages;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure.Alerts;
using WaxyCandles.Infrastructure.Persistence;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Alerts;

/// <summary>
/// Races between the AlertWorker's evaluation, the user's edits, and prices
/// evaluated out of order. Each interleaving is forced deterministically.
/// </summary>
public class AlertRaceTests(IntegrationFixture fixture) : DatabaseTestBase(fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Evaluation_does_not_fire_an_alert_the_user_edited_after_it_was_read()
    {
        // Worker reads "Above 100", sees 101 and decides to fire; before it writes,
        // the user changes the target to 110, which 101 doesn't meet.
        var alert = await SeedAsync();

        var hook = new AlertUpdateHook(() => EditAsync(alert.Id, targetPrice: 110m));
        await EvaluateAsync(101m, T0, hook);

        Assert.True(hook.Fired);

        var saved = await LoadAsync(alert.Id);
        Assert.Equal(110m, saved.TargetPrice);
        Assert.False(saved.IsTriggered);
        Assert.Equal(0, saved.TriggerCount);
        Assert.Equal(0, await EventCountAsync());
    }

    [Fact]
    public async Task Edit_rearms_even_if_the_alert_fired_after_the_edit_loaded_it()
    {
        // The API loads the alert (armed), the worker fires it for the old target
        // and commits, then the API saves the new target. The new target was
        // never reached, so the alert must end up armed for it.
        var alert = await SeedAsync();

        var hook = new AlertUpdateHook(() => EvaluateAsync(101m, DateTimeOffset.UtcNow));
        await EditAsync(alert.Id, targetPrice: 110m, hook);

        Assert.True(hook.Fired);

        var saved = await LoadAsync(alert.Id);
        Assert.Equal(110m, saved.TargetPrice);
        Assert.False(saved.IsTriggered);
        Assert.Equal(AlertStatus.Active, saved.GetStatus(DateTimeOffset.UtcNow));

        // The fire for the old target did happen, and stays in the history.
        Assert.Equal(1, saved.TriggerCount);
        Assert.Equal(1, await EventCountAsync());
    }

    [Fact]
    public async Task Older_price_evaluated_late_does_not_rearm_a_repeating_alert()
    {
        // Real sequence: 99 at T0, then 101 at T0+1m. The 101 is evaluated first
        // and fires; the delayed 99 must not re-arm it (the price is above 100).
        var alert = await SeedAsync(a => a.Frequency = AlertFrequency.EveryCross);

        await EvaluateAsync(101m, T0.AddMinutes(1));
        await EvaluateAsync(99m, T0);

        var saved = await LoadAsync(alert.Id);
        Assert.True(saved.IsTriggered);

        // A newer price below the target does re-arm it.
        await EvaluateAsync(99m, T0.AddMinutes(2));
        Assert.False((await LoadAsync(alert.Id)).IsTriggered);
    }

    [Fact]
    public async Task Older_price_evaluated_late_does_not_fire_a_rearmed_repeating_alert()
    {
        // Fired at T0 (101), re-armed at T0+20m (99). A delayed 101 observed at
        // T0+19m arrives last: even with the cooldown passed, it's older than
        // the re-arm, so it must not fire again.
        var alert = await SeedAsync(a => a.Frequency = AlertFrequency.EveryCross);

        await EvaluateAsync(101m, T0);
        await EvaluateAsync(99m, T0.AddMinutes(20));
        await EvaluateAsync(101m, T0.AddMinutes(19));

        var saved = await LoadAsync(alert.Id);
        Assert.False(saved.IsTriggered);
        Assert.Equal(1, saved.TriggerCount);
        Assert.Equal(1, await EventCountAsync());

        // A genuinely new cross after the cooldown fires.
        await EvaluateAsync(101m, T0.AddMinutes(40));
        Assert.Equal(2, (await LoadAsync(alert.Id)).TriggerCount);
    }

    [Fact]
    public async Task Price_observed_before_an_edit_cannot_fire_the_edited_alert()
    {
        var alert = await SeedAsync();
        var beforeEdit = DateTimeOffset.UtcNow.AddSeconds(-30);

        await EditAsync(alert.Id, targetPrice: 110m);

        // Meets the new target, but was observed before the user set it.
        await EvaluateAsync(111m, beforeEdit);
        Assert.False((await LoadAsync(alert.Id)).IsTriggered);

        await EvaluateAsync(111m, DateTimeOffset.UtcNow);
        Assert.True((await LoadAsync(alert.Id)).IsTriggered);
    }

    [Fact]
    public async Task Event_reports_the_condition_that_actually_fired()
    {
        var alert = await SeedAsync();

        await EvaluateAsync(101m, T0);

        var message = JsonSerializer.Deserialize<AlertTriggeredMessage>(
            (await EventsAsync()).Single().Payload)!;

        Assert.Equal(100m, message.TargetPrice);
        Assert.Equal(AlertDirection.Above, message.Direction);
        Assert.Equal(T0, message.TriggeredAt);
        Assert.Equal(T0, (await LoadAsync(alert.Id)).TriggeredAt);
    }

    private async Task EvaluateAsync(
        decimal price,
        DateTimeOffset observedAt,
        IInterceptor? interceptor = null)
    {
        await using var db = NewContext(interceptor);
        await new AlertEvaluationService(db).EvaluateAsync("AAPL", price, observedAt);
    }

    private async Task EditAsync(
        Guid id,
        decimal targetPrice,
        IInterceptor? interceptor = null)
    {
        await using var db = NewContext(interceptor);

        var updated = await new AlertService(db).UpdateAsync(
            id,
            "user-1",
            new UpdateAlertRequest
            {
                TargetPrice = targetPrice,
                Direction = AlertDirection.Above
            });

        Assert.NotNull(updated);
    }

    private AppDbContext NewContext(IInterceptor? interceptor)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(ConnectionString);

        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        return new AppDbContext(options.Options);
    }

    /// <summary>"AAPL above 100", one-time, owned by user-1, unless configured.</summary>
    private async Task<Alert> SeedAsync(Action<Alert>? configure = null)
    {
        var alert = new Alert
        {
            Id = Guid.NewGuid(),
            UserId = "user-1",
            Symbol = "AAPL",
            Direction = AlertDirection.Above,
            TargetPrice = 100m,
            CreatedAt = T0.AddDays(-1)
        };

        configure?.Invoke(alert);

        await using var db = NewDbContext();
        db.Alerts.Add(alert);
        await db.SaveChangesAsync();

        return alert;
    }

    private async Task<Alert> LoadAsync(Guid id)
    {
        await using var db = NewDbContext();
        return await db.Alerts.SingleAsync(a => a.Id == id);
    }

    private async Task<List<OutboxMessage>> EventsAsync()
    {
        await using var db = NewDbContext();
        return await db.OutboxMessages
            .Where(m => m.Type == nameof(AlertTriggeredMessage))
            .ToListAsync();
    }

    private async Task<int> EventCountAsync() => (await EventsAsync()).Count;
}
