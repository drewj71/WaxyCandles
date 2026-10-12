using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;

namespace WaxyCandles.Application.Alerts.DTOs;

public sealed class CreateAlertRequest
{
    public string Symbol { get; init; } = string.Empty;

    public decimal TargetPrice { get; init; }

    public AlertDirection Direction { get; init; }

    public AlertFrequency Frequency { get; init; } = AlertFrequency.Once;

    public int CooldownMinutes { get; init; } = Alert.DefaultCooldownMinutes;

    public DateTimeOffset? ExpiresAt { get; init; }
}
