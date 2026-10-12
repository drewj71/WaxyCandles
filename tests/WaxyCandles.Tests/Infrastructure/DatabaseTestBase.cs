using WaxyCandles.Infrastructure.Identity;
using WaxyCandles.Infrastructure.Persistence;

namespace WaxyCandles.Tests.Infrastructure;

/// <summary>
/// Base class for tests that need a real, isolated SQL Server database.
/// </summary>
[Collection(IntegrationCollection.Name)]
public abstract class DatabaseTestBase : IAsyncLifetime
{
    protected DatabaseTestBase(IntegrationFixture fixture)
    {
        Fixture = fixture;
    }

    protected IntegrationFixture Fixture { get; }

    protected string ConnectionString { get; private set; } = default!;

    public virtual async Task InitializeAsync()
    {
        ConnectionString = await Fixture.CreateDatabaseAsync();
    }

    public virtual Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// A fresh context with an empty change tracker, so assertions read what's
    /// actually in the database rather than cached entities.
    /// </summary>
    protected AppDbContext NewDbContext() =>
        IntegrationFixture.CreateDbContext(ConnectionString);

    /// <summary>
    /// Inserts a bare user row, for data with a foreign key to AspNetUsers
    /// (e.g. notifications). Not a usable login; register through the API for that.
    /// </summary>
    protected async Task<string> SeedUserAsync(string email = "user@example.com")
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant()
        };

        await using var db = NewDbContext();
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return user.Id;
    }
}
