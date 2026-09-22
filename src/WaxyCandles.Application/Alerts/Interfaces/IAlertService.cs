using WaxyCandles.Application.Alerts.DTOs;

namespace WaxyCandles.Application.Alerts.Interfaces;

public interface IAlertService
{
    Task<AlertResponse> CreateAsync(
        string userId,
        CreateAlertRequest request);

    Task<IReadOnlyList<AlertResponse>> GetAllAsync(
        string userId);

    Task<AlertResponse?> GetByIdAsync(
        Guid id,
        string userId);

    Task<AlertResponse?> UpdateAsync(
        Guid id,
        string userId,
        UpdateAlertRequest request);

    Task DeleteAsync(
        Guid id,
        string userId);
}