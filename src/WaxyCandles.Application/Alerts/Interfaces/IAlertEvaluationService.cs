namespace WaxyCandles.Application.Alerts.Interfaces;

public interface IAlertEvaluationService
{
    /// <param name="observedAt">
    /// When the price was observed. Prices can be evaluated out of order (retries,
    /// several workers), so a price older than an alert's last state change
    /// can't fire or re-arm it. Null means now.
    /// </param>
    Task EvaluateAsync(
        string symbol,
        decimal price,
        DateTimeOffset? observedAt = null,
        CancellationToken cancellationToken = default);
}
