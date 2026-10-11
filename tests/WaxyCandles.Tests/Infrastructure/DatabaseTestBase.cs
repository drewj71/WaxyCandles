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
}
