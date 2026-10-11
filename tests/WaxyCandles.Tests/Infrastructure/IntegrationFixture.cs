using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Testcontainers.MsSql;
using Testcontainers.RabbitMq;
using WaxyCandles.Infrastructure;
using WaxyCandles.Infrastructure.Persistence;

namespace WaxyCandles.Tests.Infrastructure;

/// <summary>
/// Starts one SQL Server and one RabbitMQ container for the whole test run.
/// Every test gets its own freshly migrated database, so tests never share rows.
/// </summary>
public sealed class IntegrationFixture : IAsyncLifetime
{
    // Test-only signing key. Not a real secret; never used outside this test run.
    private const string TestJwtSecret =
        "waxycandles-tests-only-signing-key-0123456789abcdef";

    private readonly MsSqlContainer _sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
        .Build();

    // The app's ConnectionFactory uses RabbitMQ's default guest/guest credentials.
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:3-management")
        .WithUsername("guest")
        .WithPassword("guest")
        .Build();

    public string RabbitMqHost => _rabbitMq.Hostname;

    public int RabbitMqPort => _rabbitMq.GetMappedPublicPort(5672);

    public Task InitializeAsync() =>
        Task.WhenAll(_sqlServer.StartAsync(), _rabbitMq.StartAsync());

    public async Task DisposeAsync()
    {
        await _sqlServer.DisposeAsync();
        await _rabbitMq.DisposeAsync();
    }

    /// <summary>
    /// Creates a uniquely named database and applies the real EF migrations to it.
    /// </summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(
            _sqlServer.GetConnectionString())
        {
            InitialCatalog = $"WaxyCandlesTests_{Guid.NewGuid():N}"
        };

        var connectionString = builder.ConnectionString;

        await using var dbContext = CreateDbContext(connectionString);
        await dbContext.Database.MigrateAsync();

        return connectionString;
    }

    public static AppDbContext CreateDbContext(string connectionString) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connectionString)
            .Options);

    /// <summary>
    /// Builds a service provider using the app's real DI wiring
    /// (AddInfrastructure + AddApiAuthentication) pointed at the test containers.
    /// </summary>
    public ServiceProvider BuildServices(
        string connectionString,
        Action<IServiceCollection>? configure = null,
        ILoggerProvider? loggerProvider = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = connectionString,
                ["Jwt:Secret"] = TestJwtSecret,
                ["Jwt:Issuer"] = "WaxyCandles.Tests",
                ["Jwt:Audience"] = "WaxyCandles.Tests",
                ["RabbitMQ:Host"] = RabbitMqHost,
                ["RabbitMQ:Port"] = RabbitMqPort.ToString()
            })
            .Build();

        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(logging =>
        {
            if (loggerProvider is not null)
            {
                logging.AddProvider(loggerProvider);
            }
        });

        services.AddInfrastructure(configuration);
        services.AddApiAuthentication(configuration);

        configure?.Invoke(services);

        return services.BuildServiceProvider(validateScopes: true);
    }
}

[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<IntegrationFixture>
{
    public const string Name = "Integration";
}
