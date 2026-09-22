namespace WaxyCandles.Application.Alerts.Interfaces;

public interface IAlertEvaluationService
{
    Task EvaluateAsync(
        string symbol,
        decimal price,
        CancellationToken cancellationToken = default);
}