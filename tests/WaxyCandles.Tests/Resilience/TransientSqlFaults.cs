using System.Data.Common;
using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using WaxyCandles.Infrastructure.Persistence;

namespace WaxyCandles.Tests.Resilience;

/// <summary>
/// A real <see cref="SqlException"/> with a chosen error number. Its
/// constructors are internal, so it's built by reflection. 1205 (deadlock
/// victim) is on EF Core's SQL Server list of transient errors.
/// </summary>
public static class TransientSqlException
{
    public const int Deadlock = 1205;

    public static SqlException Create(int number = Deadlock)
    {
        var errorCtor = typeof(SqlError)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var arguments = errorCtor.GetParameters()
            .Select(p => p.Name switch
            {
                "infoNumber" => (object?)number,
                "errorState" => (byte)1,
                "errorClass" => (byte)13,
                "errorMessage" => "Simulated transient fault (deadlock victim).",
                "lineNumber" => 0,
                _ when p.ParameterType == typeof(string) => "test",
                _ when p.ParameterType == typeof(uint) => 0u,
                _ when p.ParameterType == typeof(int) => 0,
                _ => null
            })
            .ToArray();

        var error = (SqlError)errorCtor.Invoke(arguments);

        var errors = (SqlErrorCollection)typeof(SqlErrorCollection)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(c => c.GetParameters().Length == 0)
            .Invoke(null);

        typeof(SqlErrorCollection)
            .GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(errors, [error]);

        var create = typeof(SqlException)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.Name == "CreateException")
            .OrderBy(m => m.GetParameters().Length)
            .First(m => m.GetParameters()[0].ParameterType == typeof(SqlErrorCollection));

        var createArguments = create.GetParameters()
            .Select((p, i) => i == 0
                ? errors
                : p.ParameterType == typeof(string) ? "16.0" : (object?)null)
            .ToArray();

        return (SqlException)create.Invoke(null, createArguments)!;
    }
}

/// <summary>
/// Throws a transient <see cref="SqlException"/> for the next matching
/// commands: before they reach the database or, with <paramref name="after"/>,
/// once a non-query has run (outside a transaction it has then committed).
/// </summary>
public sealed class CommandFault(Func<string, bool> matches, int times = 1, bool after = false) : DbCommandInterceptor
{
    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        if (after)
        {
            MaybeThrow(command, executing: false);
        }

        return ValueTask.FromResult(result);
    }

    private int _remaining = times;
    private int _thrown;

    /// <summary>Faults only once armed, so setup through the same services isn't affected.</summary>
    public bool Armed { get; set; } = true;

    public int Thrown => Volatile.Read(ref _thrown);

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        MaybeThrow(command, executing: true);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        MaybeThrow(command, executing: true);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        MaybeThrow(command, executing: true);
        return ValueTask.FromResult(result);
    }

    private void MaybeThrow(DbCommand command, bool executing)
    {
        if (Armed &&
            executing != after &&
            matches(command.CommandText) &&
            Interlocked.Decrement(ref _remaining) >= 0)
        {
            Interlocked.Increment(ref _thrown);
            throw TransientSqlException.Create();
        }
    }
}

/// <summary>
/// Throws a transient <see cref="SqlException"/> on the next commits: before
/// the commit (it never happens, so the transaction rolls back), or after it
/// really committed, which is the ambiguous case where the database has the
/// writes but the client saw an error.
/// </summary>
public sealed class CommitFault(bool afterCommit, int times = 1) : DbTransactionInterceptor
{
    private int _remaining = times;
    private int _thrown;

    public bool Armed { get; set; } = true;

    public int Thrown => Volatile.Read(ref _thrown);

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        if (!afterCommit)
        {
            MaybeThrow();
        }

        return ValueTask.FromResult(result);
    }

    public override Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (afterCommit)
        {
            MaybeThrow();
        }

        return Task.CompletedTask;
    }

    private void MaybeThrow()
    {
        if (Armed && Interlocked.Decrement(ref _remaining) >= 0)
        {
            Interlocked.Increment(ref _thrown);
            throw TransientSqlException.Create();
        }
    }
}

public static class FaultInjection
{
    /// <summary>Adds interceptors to the app's own AppDbContext registration.</summary>
    public static IServiceCollection AddDbInterceptors(
        this IServiceCollection services,
        params IInterceptor[] interceptors) =>
        services.ConfigureDbContext<AppDbContext>(
            (_, options) => options.AddInterceptors(interceptors));

    public static Func<string, bool> Sql(params string[] fragments) =>
        sql => fragments.All(f => sql.Contains(f, StringComparison.OrdinalIgnoreCase));
}
