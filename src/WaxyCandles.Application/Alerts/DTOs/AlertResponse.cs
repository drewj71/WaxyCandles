using WaxyCandles.Domain.Enums;

namespace WaxyCandles.Application.Alerts.DTOs;

public sealed class AlertResponse
{
    public Guid Id { get; init; }

    public string Symbol { get; init; } = string.Empty;

    public decimal TargetPrice { get; init; }

    public AlertDirection Direction { get; init; }

    public bool IsTriggered { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}