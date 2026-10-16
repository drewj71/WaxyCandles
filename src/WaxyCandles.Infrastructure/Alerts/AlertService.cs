using Microsoft.EntityFrameworkCore;
using WaxyCandles.Application.Alerts.DTOs;
using WaxyCandles.Application.Alerts.Interfaces;
using WaxyCandles.Application.Alerts.Levels;
using WaxyCandles.Application.Alerts.Validation;
using WaxyCandles.Application.Common.Exceptions;
using WaxyCandles.Application.MarketData;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Infrastructure.Persistence;

namespace WaxyCandles.Infrastructure.Alerts;

public class AlertService : IAlertService
{
    private readonly AppDbContext _dbContext;
    private readonly IGexLevelBoardSource _levelBoards;

    public AlertService(
        AppDbContext dbContext,
        IGexLevelBoardSource levelBoards)
    {
        _dbContext = dbContext;
        _levelBoards = levelBoards;
    }

    public async Task<AlertResponse> CreateAsync(
        string userId,
        CreateAlertRequest request)
    {
        var now = DateTimeOffset.UtcNow;

        AlertRequestValidator.Validate(request, now);

        var alert = new Alert
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Symbol = AlertRequestValidator.NormalizeSymbol(request.Symbol),
            TargetPrice = request.TargetPrice,
            Direction = request.Direction,
            Level = request.Level,
            LevelProximityPercent = request.Level is null ? null : request.LevelProximityPercent,
            LevelDays = request.Level is null ? null : request.LevelDays,
            LevelStrikeRange = request.Level is null ? null : request.LevelStrikeRange,
            Frequency = request.Frequency,
            CooldownMinutes = request.CooldownMinutes,
            ExpiresAt = request.ExpiresAt,
            IsEnabled = true,
            IsTriggered = false,
            CreatedAt = now
        };

        if (alert.IsLevelAlert)
        {
            await ResolveLevelAsync(alert, now, required: true);
        }

        _dbContext.Alerts.Add(alert);

        await _dbContext.SaveChangesAsync();

        return AlertResponse.From(alert, now);
    }

    public async Task<IReadOnlyList<AlertResponse>> GetAllAsync(
        string userId)
    {
        var alerts = await _dbContext.Alerts
            .AsNoTracking()
            .Where(a => a.UserId == userId)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync();

        var now = DateTimeOffset.UtcNow;

        return alerts
            .Select(a => AlertResponse.From(a, now))
            .ToList();
    }

    public async Task<AlertResponse?> GetByIdAsync(
        Guid id,
        string userId)
    {
        var alert = await _dbContext.Alerts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId);

        return alert is null
            ? null
            : AlertResponse.From(alert, DateTimeOffset.UtcNow);
    }

    public async Task<AlertResponse?> UpdateAsync(
        Guid id,
        string userId,
        UpdateAlertRequest request)
    {
        var now = DateTimeOffset.UtcNow;

        var alert = await _dbContext.Alerts
            .FirstOrDefaultAsync(a =>
                a.Id == id &&
                a.UserId == userId);

        if (alert is null)
        {
            return null;
        }

        AlertRequestValidator.Validate(request, alert.ExpiresAt, now);

        var previousTarget = alert.TargetPrice;
        var previousDirection = alert.Direction;

        var levelSettingsChanged =
            alert.Level != request.Level ||
            (request.Level is not null &&
             (alert.LevelProximityPercent != request.LevelProximityPercent ||
              alert.LevelDays != request.LevelDays ||
              alert.LevelStrikeRange != request.LevelStrikeRange));

        // The daily refresh skips disabled, expired and fired one-time alerts,
        // so one being revived (re-enabled, expiry extended, switched to
        // repeating) may have a stale target. Any edit to an alert not checked
        // today re-resolves it, keeping the old target if today's board has
        // no such level rather than blocking the edit.
        var stale = alert.LevelCheckedOn != MarketHours.TradingDate(now);

        if (request.Level is null)
        {
            alert.TargetPrice = request.TargetPrice;
            alert.Direction = request.Direction;
            alert.Level = null;
            alert.LevelProximityPercent = null;
            alert.LevelDays = null;
            alert.LevelStrikeRange = null;
            alert.LevelPrice = null;
            alert.LevelAsOf = null;
            alert.LevelCheckedOn = null;
        }
        else
        {
            alert.Level = request.Level;
            alert.LevelProximityPercent = request.LevelProximityPercent;
            alert.LevelDays = request.LevelDays;
            alert.LevelStrikeRange = request.LevelStrikeRange;

            if (levelSettingsChanged)
            {
                await ResolveLevelAsync(alert, now, required: true);
            }
            else if (stale)
            {
                await ResolveLevelAsync(alert, now, required: false);
            }
        }

        // Changing what the alert fires on re-arms it: the old trigger was for a
        // different condition. Toggling it or changing the expiry doesn't, and
        // the firing history (TriggeredAt, TriggerCount) is kept either way.
        var conditionChanged =
            alert.TargetPrice != previousTarget ||
            alert.Direction != previousDirection ||
            alert.Frequency != request.Frequency ||
            levelSettingsChanged;

        alert.Frequency = request.Frequency;
        alert.CooldownMinutes = request.CooldownMinutes;
        alert.ExpiresAt = request.ExpiresAt;
        alert.IsEnabled = request.IsEnabled;

        if (conditionChanged)
        {
            alert.IsTriggered = false;

            // Always write it: EF only writes properties that differ from what it
            // loaded, so if the worker fired the alert after we loaded it (false),
            // setting false again would be skipped and the alert left triggered
            // for a target it never reached.
            _dbContext.Entry(alert).Property(a => a.IsTriggered).IsModified = true;

            // Prices observed before this edit belong to the old condition and
            // must not fire or re-arm the edited alert.
            alert.StateChangedAt = now;
        }

        await _dbContext.SaveChangesAsync();

        return AlertResponse.From(alert, now);
    }

    /// <summary>
    /// Sets a level alert's target and direction from where its level is now.
    /// When the board has no such level: if <paramref name="required"/> (new
    /// level settings, which would otherwise have no target) throws
    /// <see cref="RequestValidationException"/>; otherwise keeps the current target.
    /// </summary>
    private async Task ResolveLevelAsync(
        Alert alert,
        DateTimeOffset now,
        bool required)
    {
        var board = await _levelBoards.GetAsync(
            alert.Symbol,
            alert.LevelDays!.Value,
            alert.LevelStrikeRange!.Value);

        var node = board is null
            ? null
            : GexLevelTarget.Pick(board.Levels, alert.Level!.Value);

        if (board is null || node is null)
        {
            if (!required)
            {
                return;
            }

            throw LevelError(board is null
                ? $"No option data for {alert.Symbol}."
                : $"{alert.Symbol} has no {alert.Level} on its GEX board right now. " +
                  "Try a wider strike range or more expirations.");
        }

        (alert.TargetPrice, alert.Direction) = GexLevelTarget.For(
            node.Strike,
            board.CurrentPrice,
            alert.LevelProximityPercent!.Value);

        alert.LevelPrice = node.Strike;
        alert.LevelAsOf = board.AsOf;
        alert.LevelCheckedOn = MarketHours.TradingDate(now);
    }

    private static RequestValidationException LevelError(string message) =>
        new(new Dictionary<string, string[]> { ["level"] = [message] });

    public async Task DeleteAsync(Guid id, string userId)
    {
        var alert = await _dbContext.Alerts
            .FirstOrDefaultAsync(a =>
                a.Id == id &&
                a.UserId == userId);

        if (alert is null)
        {
            return;
        }

        _dbContext.Alerts.Remove(alert);

        await _dbContext.SaveChangesAsync();
    }
}
