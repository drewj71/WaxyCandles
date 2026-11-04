using WaxyCandles.Application.Alerts.DTOs;

namespace WaxyCandles.Application.Alerts.Interfaces;

public interface IAlertEvaluationService
{
    /// <param name="observedAt">
    /// When the price was observed. Prices can be evaluated out of order (retries,
    /// several workers), so a price older than an alert's last state change
    /// can't fire or re-arm it. Null means now.
    /// </param>
    /// <returns>
    /// The alerts this evaluation fired. Inside a caller's transaction they are
    /// saved but not yet committed: the caller records them (e.g. the
    /// <c>alerts.fired</c> metric) only once its commit succeeds, and discards
    /// them if the attempt is rolled back or replayed. Without a caller
    /// transaction the evaluation has committed them and recorded the metric itself.
    /// </returns>
    Task<AlertEvaluationResult> EvaluateAsync(
        string symbol,
        decimal price,
        DateTimeOffset? observedAt = null,
        CancellationToken cancellationToken = default);
}
