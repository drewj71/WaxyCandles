using Microsoft.EntityFrameworkCore;
using WaxyCandles.Application.Alerts.DTOs;
using WaxyCandles.Application.Alerts.Interfaces;
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
        var alert = new Alert
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Symbol = request.Symbol.ToUpperInvariant(),
            TargetPrice = request.TargetPrice,
            Direction = request.Direction,
            IsTriggered = false,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _dbContext.Alerts.Add(alert);

        await _dbContext.SaveChangesAsync();

        return new AlertResponse
        {
            Id = alert.Id,
            Symbol = alert.Symbol,
            TargetPrice = alert.TargetPrice,
            Direction = alert.Direction,
            IsTriggered = alert.IsTriggered,
            CreatedAt = alert.CreatedAt
        };
    }

    public async Task<IReadOnlyList<AlertResponse>> GetAllAsync(
        string userId)
    {
        var alerts = await _dbContext.Alerts
            .Where(a => a.UserId == userId)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new AlertResponse
            {
                Id = a.Id,
                Symbol = a.Symbol,
                TargetPrice = a.TargetPrice,
                Direction = a.Direction,
                IsTriggered = a.IsTriggered,
                CreatedAt = a.CreatedAt
            })
            .ToListAsync();

        return alerts;
    }

    public async Task<AlertResponse?> GetByIdAsync(
        Guid id,
        string userId)
    {
        var alert = await _dbContext.Alerts
            .Where(a => a.Id == id && a.UserId == userId)
            .Select(a => new AlertResponse
            {
                Id = a.Id,
                Symbol = a.Symbol,
                TargetPrice = a.TargetPrice,
                Direction = a.Direction,
                IsTriggered = a.IsTriggered,
                CreatedAt = a.CreatedAt
            })
            .FirstOrDefaultAsync();

        return alert;

    }

    public async Task<AlertResponse?> UpdateAsync(
        Guid id,
        string userId,
        UpdateAlertRequest request)
    {
        var alert = await _dbContext.Alerts
            .FirstOrDefaultAsync(a =>
                a.Id == id &&
                a.UserId == userId);

        if (alert is null)
        {
            return null;
        }

        alert.TargetPrice = request.TargetPrice;
        alert.Direction = request.Direction;

        await _dbContext.SaveChangesAsync();

        return new AlertResponse
        {
            Id = alert.Id,
            Symbol = alert.Symbol,
            TargetPrice = alert.TargetPrice,
            Direction = alert.Direction,
            IsTriggered = alert.IsTriggered,
            CreatedAt = alert.CreatedAt
        };
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