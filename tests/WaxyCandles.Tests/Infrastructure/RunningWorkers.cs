using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace WaxyCandles.Tests.Infrastructure;

/// <summary>
/// Starts the real BackgroundService workers in-process and stops them on dispose,
/// so tests don't need a full host.
/// </summary>
public sealed class RunningWorkers : IAsyncDisposable
{
    private readonly List<(ServiceProvider Services, BackgroundService Worker)> _workers = [];

    public int Count => _workers.Count;

    /// <summary>Starts a worker. Takes ownership of <paramref name="services"/>.</summary>
    public async Task<TWorker> StartAsync<TWorker>(ServiceProvider services)
        where TWorker : BackgroundService
    {
        var worker = ActivatorUtilities.CreateInstance<TWorker>(services);
        _workers.Add((services, worker));

        await worker.StartAsync(CancellationToken.None);

        return worker;
    }

    public async Task StopAllAsync()
    {
        foreach (var (services, worker) in _workers)
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
            await services.DisposeAsync();
        }

        _workers.Clear();
    }

    public async ValueTask DisposeAsync() => await StopAllAsync();
}
