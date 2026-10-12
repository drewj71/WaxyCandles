using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace WaxyCandles.Tests.Infrastructure;

/// <summary>
/// Holds every UPDATE of the Alerts table until <c>parties</c> of them have
/// arrived (or 5s pass). Shared by several DbContexts, it forces concurrent
/// evaluations to all finish reading alerts before any of them writes: the
/// interleaving that exposes a read-then-update race.
/// </summary>
public sealed class AlertUpdateBarrier(int parties) : DbCommandInterceptor
{
    private readonly TaskCompletionSource _allArrived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _arrived;

    public int Arrived => Volatile.Read(ref _arrived);

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await WaitIfAlertUpdateAsync(command, cancellationToken);
        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await WaitIfAlertUpdateAsync(command, cancellationToken);
        return result;
    }

    private async Task WaitIfAlertUpdateAsync(DbCommand command, CancellationToken cancellationToken)
    {
        var sql = command.CommandText;

        if (!sql.Contains("UPDATE", StringComparison.OrdinalIgnoreCase) ||
            !sql.Contains("[Alerts]", StringComparison.Ordinal))
        {
            return;
        }

        if (Interlocked.Increment(ref _arrived) >= parties)
        {
            _allArrived.TrySetResult();
        }

        await Task.WhenAny(
            _allArrived.Task,
            Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
    }
}
