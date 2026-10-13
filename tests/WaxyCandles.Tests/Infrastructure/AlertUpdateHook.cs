using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace WaxyCandles.Tests.Infrastructure;

/// <summary>
/// Runs a callback once, just before the first UPDATE of the Alerts table is
/// sent: after this context has read the alert, before it writes. The callback
/// typically commits a competing change on another connection, reproducing a
/// read-then-write race deterministically.
/// </summary>
public sealed class AlertUpdateHook(Func<Task> beforeFirstUpdate) : DbCommandInterceptor
{
    private int _fired;

    public bool Fired => Volatile.Read(ref _fired) == 1;

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await RunOnceIfAlertUpdateAsync(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await RunOnceIfAlertUpdateAsync(command);
        return result;
    }

    private async Task RunOnceIfAlertUpdateAsync(DbCommand command)
    {
        var sql = command.CommandText;

        if (sql.Contains("UPDATE", StringComparison.OrdinalIgnoreCase) &&
            sql.Contains("[Alerts]", StringComparison.Ordinal) &&
            Interlocked.Exchange(ref _fired, 1) == 0)
        {
            await beforeFirstUpdate();
        }
    }
}
