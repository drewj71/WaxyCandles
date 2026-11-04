using Microsoft.EntityFrameworkCore;
using WaxyCandles.Application.Alerts.DTOs;
using WaxyCandles.Application.Alerts.Levels;
using WaxyCandles.Application.MarketData;
using WaxyCandles.Application.Options.Gex;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure.Alerts;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Alerts;

/// <summary>
/// Creating or editing an alert stamps <see cref="Alert.StateChangedAt"/> with
/// the newest stored price time for its symbol (the provider's clock), so a
/// price observed before the alert existed, or before the edit, but still in
/// the pipeline can't fire it. Real SQL Server.
/// </summary>
public class AlertStateBoundaryTests(IntegrationFixture fixture) : DatabaseTestBase(fixture)
{
    private const string UserId = "user-1";

    private readonly FakeGexLevelBoardSource _boards = new();

    // ---- Create ----

    [Fact]
    public async Task Price_observed_before_the_alert_was_created_cannot_fire_it()
    {
        var latest = DateTimeOffset.UtcNow.AddSeconds(-30);
        await StorePriceAsync("AAPL", latest.AddMinutes(-1));
        await StorePriceAsync("AAPL", latest);
        await StorePriceAsync("MSFT", latest.AddMinutes(5)); // another symbol's clock

        var created = await CreateAsync("AAPL", 100m);

        Assert.Equal(latest, (await LoadAsync(created.Id)).StateChangedAt);

        // Still in the pipeline when the alert was created: older, or the
        // newest stored price itself.
        Assert.Empty((await EvaluateAsync("AAPL", 150m, latest.AddSeconds(-1))).Fired);
        Assert.Empty((await EvaluateAsync("AAPL", 150m, latest)).Fired);

        // The next poll fires it.
        var fired = await EvaluateAsync("AAPL", 150m, latest.AddSeconds(1));
        Assert.Equal(created.Id, Assert.Single(fired.Fired).AlertId);
    }

    [Fact]
    public async Task Symbol_with_no_stored_price_has_no_boundary()
    {
        var created = await CreateAsync("AAPL", 100m);

        Assert.Null((await LoadAsync(created.Id)).StateChangedAt);

        // Nothing for the symbol could have been in the pipeline: its first
        // price fires, however its quote time compares with the wall clock.
        var fired = await EvaluateAsync("AAPL", 150m, DateTimeOffset.UtcNow.AddMinutes(-15));
        Assert.Single(fired.Fired);
    }

    [Fact]
    public async Task Delayed_feed_quotes_after_the_newest_stored_price_fire_a_new_alert()
    {
        // A 15-minute delayed feed: quote times trail the wall clock.
        var latest = DateTimeOffset.UtcNow.AddMinutes(-15);
        await StorePriceAsync("AAPL", latest);

        await CreateAsync("AAPL", 100m);

        // A wall-clock stamp would mute the alert until quotes caught up.
        var fired = await EvaluateAsync("AAPL", 150m, latest.AddMinutes(1));
        Assert.Single(fired.Fired);
    }

    [Fact]
    public async Task Level_alert_is_stamped_on_create_too()
    {
        var latest = DateTimeOffset.UtcNow.AddMinutes(-2);
        await StorePriceAsync("SPY", latest);
        _boards.Boards["SPY"] = new GexLevelBoard(
            new GexLevels(
                new GexNode(600m, new DateOnly(2026, 10, 16), -5_000_000),
                Floor: null,
                Ceiling: null,
                Gatekeeper: null),
            DateTimeOffset.UtcNow.AddHours(-12),
            580m);

        var created = await Service().CreateAsync(UserId, new CreateAlertRequest
        {
            Symbol = "SPY",
            Level = GexLevelKind.King
        });

        // The stock price clock, not the option chain's AsOf.
        Assert.Equal(latest, (await LoadAsync(created.Id)).StateChangedAt);
    }

    // ---- Edit ----

    [Fact]
    public async Task Edit_stamps_the_newest_stored_price_time()
    {
        var created = await CreateAsync("AAPL", 100m);

        // Quotes lagging the wall clock.
        var latest = DateTimeOffset.UtcNow.AddMinutes(-15);
        await StorePriceAsync("AAPL", latest);

        await Service().UpdateAsync(created.Id, UserId, Update(target: 120m));

        Assert.Equal(latest, (await LoadAsync(created.Id)).StateChangedAt);
        Assert.Empty((await EvaluateAsync("AAPL", 150m, latest)).Fired);
        Assert.Single((await EvaluateAsync("AAPL", 150m, latest.AddSeconds(1))).Fired);
    }

    [Fact]
    public async Task Edit_never_moves_the_boundary_back()
    {
        var stored = DateTimeOffset.UtcNow.AddMinutes(-10);
        await StorePriceAsync("AAPL", stored);
        var created = await CreateAsync("AAPL", 100m);

        // A state change later than any stored price, e.g. an earlier edit
        // stamped with the wall clock before the symbol had a price.
        var later = DateTimeOffset.UtcNow.AddMinutes(1);
        await using (var db = NewDbContext())
        {
            await db.Alerts
                .Where(a => a.Id == created.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.StateChangedAt, later));
        }

        await Service().UpdateAsync(created.Id, UserId, Update(target: 120m));

        Assert.Equal(later, (await LoadAsync(created.Id)).StateChangedAt);
    }

    [Fact]
    public async Task Edit_of_a_symbol_with_no_stored_price_falls_back_to_now()
    {
        var created = await CreateAsync("AAPL", 100m);
        var before = DateTimeOffset.UtcNow;

        await Service().UpdateAsync(created.Id, UserId, Update(target: 120m));

        var stamp = (await LoadAsync(created.Id)).StateChangedAt;
        Assert.NotNull(stamp);
        Assert.InRange(stamp.Value, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task Edit_that_does_not_change_the_condition_keeps_the_stamp()
    {
        await StorePriceAsync("AAPL", DateTimeOffset.UtcNow.AddMinutes(-10));
        var created = await CreateAsync("AAPL", 100m);
        var stamped = (await LoadAsync(created.Id)).StateChangedAt;

        await StorePriceAsync("AAPL", DateTimeOffset.UtcNow.AddMinutes(-5));
        await Service().UpdateAsync(created.Id, UserId, Update(target: 100m, enabled: false));

        Assert.Equal(stamped, (await LoadAsync(created.Id)).StateChangedAt);
    }

    // ---- helpers ----

    private AlertService Service() => new(NewDbContext(), _boards);

    private Task<AlertResponse> CreateAsync(string symbol, decimal target) =>
        Service().CreateAsync(UserId, new CreateAlertRequest
        {
            Symbol = symbol,
            TargetPrice = target,
            Direction = AlertDirection.Above
        });

    private static UpdateAlertRequest Update(decimal target, bool enabled = true) => new()
    {
        TargetPrice = target,
        Direction = AlertDirection.Above,
        IsEnabled = enabled
    };

    private async Task<AlertEvaluationResult> EvaluateAsync(
        string symbol,
        decimal price,
        DateTimeOffset observedAt)
    {
        await using var db = NewDbContext();
        return await new AlertEvaluationService(db).EvaluateAsync(symbol, price, observedAt);
    }

    private async Task StorePriceAsync(string symbol, DateTimeOffset timestamp)
    {
        await using var db = NewDbContext();
        db.StockPrices.Add(new StockPrice
        {
            Id = Guid.NewGuid(),
            Symbol = symbol,
            Price = 99m,
            Timestamp = timestamp
        });
        await db.SaveChangesAsync();
    }

    private async Task<Alert> LoadAsync(Guid id)
    {
        await using var db = NewDbContext();
        return await db.Alerts.AsNoTracking().SingleAsync(a => a.Id == id);
    }
}
