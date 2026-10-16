namespace WaxyCandles.Application.Alerts.Interfaces;

public interface IGexLevelAlertRefresher
{
    /// <summary>
    /// Re-resolves the target of every watching GEX level alert from today's
    /// board. Returns false if any board couldn't be loaded; those alerts keep
    /// their previous target.
    /// </summary>
    Task<bool> RefreshAsync(CancellationToken cancellationToken = default);
}
