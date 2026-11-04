using Microsoft.EntityFrameworkCore;

namespace WaxyCandles.Infrastructure.Persistence;

public static class VerifiedSaveExtensions
{
    /// <summary>
    /// <c>SaveChanges</c> through the execution strategy, in its own
    /// transaction, with a check for the ambiguous commit: the commit reached
    /// the database but the client saw a transient error. A plain retry would
    /// then insert the same rows again and fail with a duplicate key although
    /// the save worked. Instead <paramref name="saved"/> asks the database
    /// whether the write is there, and if so the save counts as done.
    /// </summary>
    /// <param name="saved">
    /// True if this save's writes are in the database, e.g. "the row with this
    /// Id exists". Runs only after a failure, on the same context.
    /// </param>
    /// <remarks>
    /// Changes are accepted only once the save succeeds, so a retry after a
    /// failure before the commit writes the same changes again. Exceptions that
    /// aren't transient (e.g. a real duplicate key) propagate as from
    /// <c>SaveChangesAsync</c>.
    /// </remarks>
    public static async Task SaveChangesVerifiedAsync(
        this DbContext dbContext,
        Func<CancellationToken, Task<bool>> saved,
        CancellationToken cancellationToken = default)
    {
        await dbContext.Database.CreateExecutionStrategy().ExecuteInTransactionAsync(
            ct => dbContext.SaveChangesAsync(acceptAllChangesOnSuccess: false, ct),
            saved,
            cancellationToken);

        dbContext.ChangeTracker.AcceptAllChanges();
    }
}
