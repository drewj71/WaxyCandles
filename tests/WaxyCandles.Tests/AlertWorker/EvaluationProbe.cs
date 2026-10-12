using System.Collections.Concurrent;
using WaxyCandles.Application.Alerts.Interfaces;

namespace WaxyCandles.Tests.AlertWorker;

/// <summary>
/// Shared across consumer instances: records every evaluation attempt and lets a
/// test inject failures or hold an attempt open before the real evaluation runs.
/// </summary>
public sealed class EvaluationProbe
{
    private readonly ConcurrentQueue<string> _calls = new();

    public IReadOnlyCollection<string> Calls => _calls.ToArray();

    /// <summary>Runs before the real evaluation. Receives the 1-based attempt number.</summary>
    public Func<int, Task>? BeforeEvaluate { get; set; }

    /// <summary>
    /// Runs after the real evaluation has saved its changes (inside the consumer's
    /// transaction). Receives the 1-based attempt number.
    /// </summary>
    public Func<int, Task>? AfterEvaluate { get; set; }

    internal async Task OnEvaluateAsync(string symbol)
    {
        _calls.Enqueue(symbol);

        if (BeforeEvaluate is not null)
        {
            await BeforeEvaluate(_calls.Count);
        }
    }

    internal async Task OnEvaluatedAsync()
    {
        if (AfterEvaluate is not null)
        {
            await AfterEvaluate(_calls.Count);
        }
    }
}

public sealed class ProbingAlertEvaluationService(
    IAlertEvaluationService inner,
    EvaluationProbe probe) : IAlertEvaluationService
{
    public async Task EvaluateAsync(
        string symbol,
        decimal price,
        CancellationToken cancellationToken = default)
    {
        await probe.OnEvaluateAsync(symbol);
        await inner.EvaluateAsync(symbol, price, cancellationToken);
        await probe.OnEvaluatedAsync();
    }
}
