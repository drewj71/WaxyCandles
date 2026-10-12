using WaxyCandles.Domain.Entities;
using WaxyCandles.Domain.Enums;

namespace WaxyCandles.Application.Alerts.DTOs;

/// <summary>
/// Replaces the alert's settings (PUT semantics: omitted fields take their
/// defaults, so a missing ExpiresAt clears the expiry). The symbol can't change.
/// </summary>
public sealed class UpdateAlertRequest
{
    public decimal TargetPrice { get; init; }

    public AlertDirection Direction { get; init; }

    public AlertFrequency Frequency { get; init; } = AlertFrequency.Once;

    public int CooldownMinutes { get; init; } = Alert.DefaultCooldownMinutes;

    public DateTimeOffset? ExpiresAt { get; init; }

    public bool IsEnabled { get; init; } = true;
}
