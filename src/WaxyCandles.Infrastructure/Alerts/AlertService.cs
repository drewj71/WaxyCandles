using Microsoft.EntityFrameworkCore;
using WaxyCandles.Application.Alerts.DTOs;
using WaxyCandles.Application.Alerts.Interfaces;
using WaxyCandles.Application.Alerts.Validation;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Infrastructure.Persistence;

namespace WaxyCandles.Infrastructure.Alerts;

public class AlertService : IAlertService
{
    private readonly AppDbContext _dbContext;

    public AlertService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
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
            Frequency = request.Frequency,
            CooldownMinutes = request.CooldownMinutes,
            ExpiresAt = request.ExpiresAt,
            IsEnabled = true,
            IsTriggered = false,
            CreatedAt = now
        };

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

        // Changing what the alert fires on re-arms it: the old trigger was for a
        // different condition. Toggling it or changing the expiry doesn't, and
        // the firing history (TriggeredAt, TriggerCount) is kept either way.
        var conditionChanged =
            alert.TargetPrice != request.TargetPrice ||
            alert.Direction != request.Direction ||
            alert.Frequency != request.Frequency;

        alert.TargetPrice = request.TargetPrice;
        alert.Direction = request.Direction;
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
