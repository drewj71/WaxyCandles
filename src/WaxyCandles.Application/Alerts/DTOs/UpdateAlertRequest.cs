using WaxyCandles.Domain.Enums;

namespace WaxyCandles.Application.Alerts.DTOs;

public sealed class UpdateAlertRequest
{
    public decimal TargetPrice { get; init; }

    public AlertDirection Direction { get; init; }
}