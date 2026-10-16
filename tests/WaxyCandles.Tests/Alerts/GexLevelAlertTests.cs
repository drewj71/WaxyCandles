using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using WaxyCandles.Application.Alerts.DTOs;
using WaxyCandles.Application.Alerts.Levels;
using WaxyCandles.Application.Alerts.Messages;
using WaxyCandles.Application.Common.Exceptions;
using WaxyCandles.Application.MarketData;
using WaxyCandles.Application.Options.Gex;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;
using WaxyCandles.Infrastructure.Alerts;
using WaxyCandles.Infrastructure.Persistence;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Alerts;

/// <summary>
/// GEX level alerts against SQL Server: resolving the level on create and
/// edit, the daily refresh, and how a refresh interacts with evaluation.
/// </summary>
public class GexLevelAlertTests(IntegrationFixture fixture) : DatabaseTestBase(fixture)
{
    private const string UserId = "user-1";

    private static readonly DateOnly Expiration = new(2026, 10, 16);
    private static readonly DateTimeOffset ChainAsOf = new(2026, 10, 15, 20, 0, 0, TimeSpan.Zero);

    private readonly FakeGexLevelBoardSource _boards = new();

    /// <summary>A board with a King at <paramref name="king"/>; other levels only if given.</summary>
    private static GexLevelBoard Board(
        decimal king,
        decimal currentPrice,
        decimal? gatekeeper = null,
        DateTimeOffset? asOf = null) =>
        new(
            new GexLevels(
                new GexNode(king, Expiration, -5_000_000),
                Floor: null,
                Ceiling: null,
                Gatekeeper: gatekeeper is { } g ? new GexNode(g, Expiration, 1_000_000) : null),
            asOf ?? ChainAsOf,
            currentPrice);

    // ---- Create and update ----

    [Fact]
    public async Task Creating_a_level_alert_derives_its_target_from_the_level()
    {
        _boards.Boards["SPY"] = Board(king: 600m, currentPrice: 580m);

        var response = await Service().CreateAsync(UserId, new CreateAlertRequest
        {
            Symbol = "spy",
            Level = GexLevelKind.King,
            LevelProximityPercent = 0.5m,
            LevelDays = 7,
            LevelStrikeRange = 20m
        });

        Assert.Equal(597m, response.TargetPrice);
        Assert.Equal(AlertDirection.Above, response.Direction);
        Assert.Equal(GexLevelKind.King, response.Level);
        Assert.Equal(600m, response.LevelPrice);
        Assert.Equal(ChainAsOf, response.LevelAsOf);
        Assert.Equal([("SPY", 7, 20m)], _boards.Requests);

        var saved = await LoadAsync(response.Id);
        Assert.Equal(597m, saved.TargetPrice);
        Assert.Equal(7, saved.LevelDays);
        Assert.Equal(20m, saved.LevelStrikeRange);
    }

    [Fact]
    public async Task Level_missing_from_the_board_is_rejected_without_saving()
    {
        _boards.Boards["SPY"] = Board(king: 600m, currentPrice: 580m);

        var ex = await Assert.ThrowsAsync<RequestValidationException>(() =>
            Service().CreateAsync(UserId, LevelRequest("SPY", GexLevelKind.Gatekeeper)));

        Assert.Contains("no Gatekeeper", ex.Errors["level"].Single());
        Assert.Equal(0, await AlertCountAsync());
    }

    [Fact]
    public async Task Symbol_without_option_data_is_rejected()
    {
        var ex = await Assert.ThrowsAsync<RequestValidationException>(() =>
            Service().CreateAsync(UserId, LevelRequest("NOPE", GexLevelKind.King)));

        Assert.Equal("No option data for NOPE.", ex.Errors["level"].Single());
    }

    [Theory]
    [InlineData(-0.1, 30, 10, "levelProximityPercent")]
    [InlineData(5.1, 30, 10, "levelProximityPercent")]
    [InlineData(0.5, 366, 10, "levelDays")]
    [InlineData(0.5, 30, 0, "levelStrikeRange")]
    public async Task Invalid_level_settings_are_rejected_before_loading_a_board(
        decimal proximity, int days, decimal range, string field)
    {
        var ex = await Assert.ThrowsAsync<RequestValidationException>(() =>
            Service().CreateAsync(UserId, new CreateAlertRequest
            {
                Symbol = "SPY",
                Level = GexLevelKind.King,
                LevelProximityPercent = proximity,
                LevelDays = days,
                LevelStrikeRange = range
            }));

        Assert.Contains(field, ex.Errors.Keys);
        // Target and direction aren't required for a level alert.
        Assert.DoesNotContain("targetPrice", ex.Errors.Keys);
        Assert.Empty(_boards.Requests);
    }

    [Fact]
    public async Task Price_alerts_never_load_a_board()
    {
        var response = await Service().CreateAsync(UserId, new CreateAlertRequest
        {
            Symbol = "SPY",
            TargetPrice = 600m,
            Direction = AlertDirection.Above
        });

        Assert.Null(response.Level);
        Assert.Null(response.LevelDays);
        Assert.Empty(_boards.Requests);
    }

    [Fact]
    public async Task Changing_level_settings_re_resolves_and_re_arms()
    {
        _boards.Boards["SPY"] = Board(king: 600m, currentPrice: 580m);
        var alert = await SeedLevelAlertAsync(configure: a => a.IsTriggered = true);

        var response = await Service().UpdateAsync(alert.Id, UserId, LevelUpdate(proximity: 1m));

        Assert.Equal(594m, response!.TargetPrice);
        Assert.False(response.IsTriggered);
        Assert.NotNull((await LoadAsync(alert.Id)).StateChangedAt);
    }

    [Fact]
    public async Task Unchanged_level_settings_keep_the_target_without_loading_a_board()
    {
        var alert = await SeedLevelAlertAsync(configure: a =>
        {
            a.IsTriggered = true;
            a.LevelCheckedOn = Today;
        });

        var response = await Service().UpdateAsync(
            alert.Id,
            UserId,
            LevelUpdate(expiresAt: DateTimeOffset.UtcNow.AddDays(3)));

        Assert.Equal(alert.TargetPrice, response!.TargetPrice);
        Assert.True(response.IsTriggered);
        Assert.Empty(_boards.Requests);
    }

    [Fact]
    public async Task Re_enabling_a_level_alert_re_resolves_its_stale_level()
    {
        _boards.Boards["SPY"] = Board(king: 620m, currentPrice: 630m);
        var alert = await SeedLevelAlertAsync(configure: a => a.IsEnabled = false);

        var response = await Service().UpdateAsync(alert.Id, UserId, LevelUpdate());

        Assert.Equal(620m, response!.LevelPrice);
        Assert.Equal(623.1m, response.TargetPrice);
        Assert.Equal(AlertDirection.Below, response.Direction);
    }

    [Fact]
    public async Task Reviving_a_stale_alert_keeps_its_target_if_the_level_is_gone_today()
    {
        // Re-enabling must not be blocked because today's board lacks the level.
        _boards.Boards["SPY"] = Board(king: 620m, currentPrice: 630m);
        var alert = await SeedLevelAlertAsync(configure: a =>
        {
            a.Level = GexLevelKind.Gatekeeper;
            a.IsEnabled = false;
        });

        var response = await Service().UpdateAsync(alert.Id, UserId, LevelUpdate(level: GexLevelKind.Gatekeeper));

        Assert.True(response!.IsEnabled);
        Assert.Equal(597m, response.TargetPrice);
        Assert.Equal(600m, response.LevelPrice);
    }

    [Fact]
    public async Task Extending_an_expired_stale_alert_re_resolves_it()
    {
        _boards.Boards["SPY"] = Board(king: 620m, currentPrice: 630m);
        var alert = await SeedLevelAlertAsync(configure: a => a.ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1));

        var response = await Service().UpdateAsync(
            alert.Id,
            UserId,
            LevelUpdate(expiresAt: DateTimeOffset.UtcNow.AddDays(7)));

        Assert.Equal(620m, response!.LevelPrice);
        Assert.Equal(Today, (await LoadAsync(alert.Id)).LevelCheckedOn);
    }

    [Fact]
    public async Task Switching_to_a_price_alert_clears_the_level()
    {
        var alert = await SeedLevelAlertAsync();

        var response = await Service().UpdateAsync(alert.Id, UserId, new UpdateAlertRequest
        {
            TargetPrice = 650m,
            Direction = AlertDirection.Above
        });

        Assert.Equal(650m, response!.TargetPrice);
        Assert.Null(response.Level);

        var saved = await LoadAsync(alert.Id);
        Assert.Null(saved.LevelPrice);
        Assert.Null(saved.LevelAsOf);
        Assert.Null(saved.LevelProximityPercent);
    }

    // ---- Daily refresh ----

    [Fact]
    public async Task Refresh_moves_the_target_to_todays_level_and_re_arms()
    {
        var alert = await SeedLevelAlertAsync(configure: a =>
        {
            a.Frequency = AlertFrequency.EveryCross;
            a.IsTriggered = true;
        });
        var newAsOf = ChainAsOf.AddDays(1);
        _boards.Boards["SPY"] = Board(king: 610m, currentPrice: 590m, asOf: newAsOf);
        var refreshedAt = DateTimeOffset.UtcNow;

        Assert.True(await RefreshAsync(refreshedAt));

        var saved = await LoadAsync(alert.Id);
        Assert.Equal(606.95m, saved.TargetPrice);
        Assert.Equal(AlertDirection.Above, saved.Direction);
        Assert.Equal(610m, saved.LevelPrice);
        Assert.Equal(newAsOf, saved.LevelAsOf);
        Assert.Equal(MarketHours.TradingDate(refreshedAt), saved.LevelCheckedOn);
        Assert.False(saved.IsTriggered);
        Assert.Equal(refreshedAt, saved.StateChangedAt);
    }

    [Fact]
    public async Task Alert_already_checked_today_is_not_refreshed_again()
    {
        // A worker restart or retry mid-session. Since the open the price has
        // passed through the King and the alert fired; re-resolving now would
        // flip it to "Below" and fire the same approach a second time.
        var alert = await SeedLevelAlertAsync(configure: a =>
        {
            a.Frequency = AlertFrequency.EveryCross;
            a.IsTriggered = true;
            a.LevelCheckedOn = Today;
        });
        _boards.Boards["SPY"] = Board(king: 600m, currentPrice: 601m);

        Assert.True(await RefreshAsync(DateTimeOffset.UtcNow));

        var saved = await LoadAsync(alert.Id);
        Assert.Equal(AlertDirection.Above, saved.Direction);
        Assert.True(saved.IsTriggered);
        Assert.Empty(_boards.Requests);
    }

    [Fact]
    public async Task Retry_reloads_only_the_boards_that_failed()
    {
        await SeedLevelAlertAsync();
        var qqq = await SeedLevelAlertAsync(symbol: "QQQ");
        await SeedLevelAlertAsync(symbol: "IWM");
        _boards.Failing.Add("SPY");
        _boards.Boards["QQQ"] = Board(king: 500m, currentPrice: 490m);
        // IWM: no data, which isn't retried either.

        Assert.False(await RefreshAsync(DateTimeOffset.UtcNow));

        _boards.Failing.Clear();
        _boards.Boards["SPY"] = Board(king: 610m, currentPrice: 590m);

        Assert.True(await RefreshAsync(DateTimeOffset.UtcNow));

        // The first run asked for all three (in no set order); the retry only for SPY.
        Assert.Equal(4, _boards.Requests.Count);
        Assert.Equal("SPY", _boards.Requests[3].Symbol);
        Assert.Single(_boards.Requests, r => r.Symbol == "QQQ");
        Assert.Single(_boards.Requests, r => r.Symbol == "IWM");
        Assert.Equal(497.5m, (await LoadAsync(qqq.Id)).TargetPrice);
    }

    [Fact]
    public async Task State_change_is_stamped_with_the_latest_stored_price_time()
    {
        // Quotes lag the wall clock (delayed feed). Stamping wall-clock time
        // would ignore every quote until quote time caught up with it.
        var lastQuote = DateTimeOffset.UtcNow.AddMinutes(-15);
        await SeedPriceAsync("SPY", lastQuote);
        var alert = await SeedLevelAlertAsync();
        _boards.Boards["SPY"] = Board(king: 610m, currentPrice: 590m);

        await RefreshAsync(DateTimeOffset.UtcNow);

        Assert.Equal(lastQuote, (await LoadAsync(alert.Id)).StateChangedAt);

        // The next quote, still 14 minutes behind the wall clock, can fire it.
        await EvaluateAsync(607m, lastQuote.AddMinutes(1));
        Assert.True((await LoadAsync(alert.Id)).IsTriggered);
    }

    [Fact]
    public async Task State_change_is_never_moved_back()
    {
        var later = DateTimeOffset.UtcNow.AddMinutes(-1);
        await SeedPriceAsync("SPY", DateTimeOffset.UtcNow.AddMinutes(-15));
        var alert = await SeedLevelAlertAsync(configure: a => a.StateChangedAt = later);
        _boards.Boards["SPY"] = Board(king: 610m, currentPrice: 590m);

        await RefreshAsync(DateTimeOffset.UtcNow);

        var saved = await LoadAsync(alert.Id);
        Assert.Equal(606.95m, saved.TargetPrice);
        Assert.Equal(later, saved.StateChangedAt);
    }

    [Fact]
    public async Task Refresh_with_an_unchanged_level_only_updates_its_date()
    {
        var alert = await SeedLevelAlertAsync(configure: a =>
        {
            a.Frequency = AlertFrequency.EveryCross;
            a.IsTriggered = true;
        });
        var newAsOf = ChainAsOf.AddDays(1);
        _boards.Boards["SPY"] = Board(king: 600m, currentPrice: 580m, asOf: newAsOf);

        await RefreshAsync(DateTimeOffset.UtcNow);

        var saved = await LoadAsync(alert.Id);
        Assert.Equal(newAsOf, saved.LevelAsOf);
        Assert.NotNull(saved.LevelCheckedOn);
        // Still fired for the same level: it re-arms when the price moves back.
        Assert.True(saved.IsTriggered);
        Assert.Null(saved.StateChangedAt);
    }

    [Fact]
    public async Task Refresh_leaves_a_fired_one_time_alert_alone()
    {
        var alert = await SeedLevelAlertAsync(configure: a => a.IsTriggered = true);
        _boards.Boards["SPY"] = Board(king: 610m, currentPrice: 590m);

        await RefreshAsync(DateTimeOffset.UtcNow);

        var saved = await LoadAsync(alert.Id);
        Assert.Equal(597m, saved.TargetPrice);
        Assert.True(saved.IsTriggered);
        Assert.Empty(_boards.Requests);
    }

    [Fact]
    public async Task Refresh_loads_each_board_once_however_many_alerts_use_it()
    {
        await SeedLevelAlertAsync();
        await SeedLevelAlertAsync(configure: a => a.LevelProximityPercent = 1m);
        await SeedLevelAlertAsync(configure: a => a.LevelDays = 90);
        await SeedLevelAlertAsync(configure: a => a.Level = null);
        _boards.Boards["SPY"] = Board(king: 600m, currentPrice: 580m);

        await RefreshAsync(DateTimeOffset.UtcNow);

        Assert.Equal(2, _boards.Requests.Count);
        Assert.Contains(("SPY", 30, 10m), _boards.Requests);
        Assert.Contains(("SPY", 90, 10m), _boards.Requests);
    }

    [Fact]
    public async Task Failed_board_keeps_the_previous_target_and_reports_failure()
    {
        var failing = await SeedLevelAlertAsync();
        var other = await SeedLevelAlertAsync(symbol: "QQQ");
        _boards.Failing.Add("SPY");
        _boards.Boards["QQQ"] = Board(king: 500m, currentPrice: 490m);

        Assert.False(await RefreshAsync(DateTimeOffset.UtcNow));

        Assert.Equal(597m, (await LoadAsync(failing.Id)).TargetPrice);
        Assert.Equal(497.5m, (await LoadAsync(other.Id)).TargetPrice);
    }

    [Fact]
    public async Task No_data_or_a_missing_level_keeps_the_previous_target_without_failing()
    {
        var noData = await SeedLevelAlertAsync();
        var noGatekeeper = await SeedLevelAlertAsync(
            symbol: "QQQ",
            configure: a => a.Level = GexLevelKind.Gatekeeper);
        _boards.Boards["QQQ"] = Board(king: 500m, currentPrice: 490m);

        Assert.True(await RefreshAsync(DateTimeOffset.UtcNow));

        Assert.Equal(597m, (await LoadAsync(noData.Id)).TargetPrice);
        Assert.Equal(597m, (await LoadAsync(noGatekeeper.Id)).TargetPrice);
    }

    [Fact]
    public async Task Refresh_does_not_overwrite_an_edit_made_while_it_ran()
    {
        // The refresher reads the alert, then while it loads the board the user
        // changes the proximity (and the API re-resolves it).
        var alert = await SeedLevelAlertAsync();
        _boards.Boards["SPY"] = Board(king: 610m, currentPrice: 590m);

        var refresher = Refresher(DateTimeOffset.UtcNow);
        var edited = false;
        _boards.OnRequest = async () =>
        {
            if (edited)
            {
                return;
            }

            edited = true;
            await using var db = NewDbContext();
            await new AlertService(db, new FakeGexLevelBoardSource
            {
                Boards = { ["SPY"] = Board(king: 610m, currentPrice: 590m) }
            }).UpdateAsync(alert.Id, UserId, LevelUpdate(proximity: 2m));
        };

        await refresher.RefreshAsync();

        var saved = await LoadAsync(alert.Id);
        Assert.Equal(2m, saved.LevelProximityPercent);
        Assert.Equal(597.8m, saved.TargetPrice);
    }

    // ---- Evaluation ----

    [Fact]
    public async Task Price_observed_before_a_refresh_cannot_fire_against_the_new_target()
    {
        var alert = await SeedLevelAlertAsync();
        _boards.Boards["SPY"] = Board(king: 610m, currentPrice: 590m);
        var refreshedAt = DateTimeOffset.UtcNow;
        await RefreshAsync(refreshedAt);

        // 607 meets the new target (Above 606.95), but was observed before it was set.
        await EvaluateAsync(607m, refreshedAt.AddSeconds(-30));
        Assert.False((await LoadAsync(alert.Id)).IsTriggered);

        await EvaluateAsync(607m, refreshedAt.AddSeconds(30));
        Assert.True((await LoadAsync(alert.Id)).IsTriggered);

        var message = JsonSerializer.Deserialize<AlertTriggeredMessage>(
            (await EventsAsync()).Single().Payload)!;

        Assert.Equal(GexLevelKind.King, message.Level);
        Assert.Equal(610m, message.LevelPrice);
        Assert.Equal(606.95m, message.TargetPrice);
    }

    [Fact]
    public async Task Fire_in_flight_against_the_old_target_fails_if_a_refresh_lands_first()
    {
        // The worker reads "Above 597" and sees 598; before its update, the
        // refresh moves the target to Above 606.95, which 598 doesn't meet.
        var alert = await SeedLevelAlertAsync();
        _boards.Boards["SPY"] = Board(king: 610m, currentPrice: 590m);

        var hook = new AlertUpdateHook(() => RefreshAsync(DateTimeOffset.UtcNow.AddSeconds(-5)));
        await EvaluateAsync(598m, DateTimeOffset.UtcNow, hook);

        Assert.True(hook.Fired);

        var saved = await LoadAsync(alert.Id);
        Assert.Equal(606.95m, saved.TargetPrice);
        Assert.False(saved.IsTriggered);
        Assert.Equal(0, saved.TriggerCount);
        Assert.Empty(await EventsAsync());
    }

    // ---- Helpers ----

    private static CreateAlertRequest LevelRequest(string symbol, GexLevelKind level) => new()
    {
        Symbol = symbol,
        Level = level
    };

    private static DateOnly Today => MarketHours.TradingDate(DateTimeOffset.UtcNow);

    /// <summary>The seeded alert's settings, with the given changes.</summary>
    private static UpdateAlertRequest LevelUpdate(
        decimal proximity = 0.5m,
        DateTimeOffset? expiresAt = null,
        GexLevelKind level = GexLevelKind.King) => new()
    {
        Level = level,
        LevelProximityPercent = proximity,
        LevelDays = 30,
        LevelStrikeRange = 10m,
        ExpiresAt = expiresAt
    };

    /// <summary>"SPY approaching the King (600) within 0.5%": Above 597, resolved yesterday.</summary>
    private async Task<Alert> SeedLevelAlertAsync(
        string symbol = "SPY",
        Action<Alert>? configure = null)
    {
        var alert = new Alert
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            Symbol = symbol,
            TargetPrice = 597m,
            Direction = AlertDirection.Above,
            Level = GexLevelKind.King,
            LevelProximityPercent = 0.5m,
            LevelDays = 30,
            LevelStrikeRange = 10m,
            LevelPrice = 600m,
            LevelAsOf = ChainAsOf,
            CreatedAt = ChainAsOf
        };

        configure?.Invoke(alert);

        await using var db = NewDbContext();
        db.Alerts.Add(alert);
        await db.SaveChangesAsync();

        return alert;
    }

    private async Task SeedPriceAsync(string symbol, DateTimeOffset timestamp)
    {
        await using var db = NewDbContext();
        db.StockPrices.Add(new StockPrice
        {
            Id = Guid.NewGuid(),
            Symbol = symbol,
            Price = 590m,
            Timestamp = timestamp
        });
        await db.SaveChangesAsync();
    }

    private AlertService Service() => new(NewDbContext(), _boards);

    private GexLevelAlertRefresher Refresher(DateTimeOffset now) =>
        new(
            NewDbContext(),
            _boards,
            new FixedTimeProvider(now),
            NullLogger<GexLevelAlertRefresher>.Instance);

    private Task<bool> RefreshAsync(DateTimeOffset now) => Refresher(now).RefreshAsync();

    private async Task EvaluateAsync(
        decimal price,
        DateTimeOffset observedAt,
        IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(ConnectionString);

        if (interceptor is not null)
        {
            options.AddInterceptors(interceptor);
        }

        await using var db = new AppDbContext(options.Options);
        await new AlertEvaluationService(db).EvaluateAsync("SPY", price, observedAt);
    }

    private async Task<Alert> LoadAsync(Guid id)
    {
        await using var db = NewDbContext();
        return await db.Alerts.SingleAsync(a => a.Id == id);
    }

    private async Task<int> AlertCountAsync()
    {
        await using var db = NewDbContext();
        return await db.Alerts.CountAsync();
    }

    private async Task<List<OutboxMessage>> EventsAsync()
    {
        await using var db = NewDbContext();
        return await db.OutboxMessages
            .Where(m => m.Type == nameof(AlertTriggeredMessage))
            .ToListAsync();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
