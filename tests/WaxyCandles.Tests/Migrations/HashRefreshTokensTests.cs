using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WaxyCandles.Application.Authentication.DTOs;
using WaxyCandles.Application.Authentication.Exceptions;
using WaxyCandles.Infrastructure.Authentication;
using WaxyCandles.Infrastructure.Identity;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Migrations;

/// <summary>
/// Runs the real migration against SQL Server: seed plaintext refresh tokens
/// the way the old code stored them, apply the migration, and check that the
/// hashes computed in SQL match TokenService.HashRefreshToken, so existing
/// sessions keep working.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class HashRefreshTokensTests(IntegrationFixture fixture)
{
    private const string MigrationBefore = "20261102232654_OutboxIndexesAndRetention";

    // Contains every non-alphanumeric base64 character.
    private const string FixedToken = "ab+/cd==";

    [Fact]
    public async Task Existing_plaintext_tokens_are_hashed_and_still_refresh()
    {
        var connectionString = await fixture.CreateDatabaseAsync(MigrationBefore);
        var userId = await SeedUserAsync(connectionString);

        var generated = new TokenService(new ConfigurationBuilder().Build()).GenerateRefreshToken();
        var revoked = Convert.ToBase64String(Guid.NewGuid().ToByteArray());

        await SeedPlaintextTokenAsync(connectionString, userId, generated, revokedAt: null);
        await SeedPlaintextTokenAsync(connectionString, userId, FixedToken, revokedAt: null);
        await SeedPlaintextTokenAsync(connectionString, userId, revoked, revokedAt: DateTimeOffset.UtcNow);

        await using (var db = IntegrationFixture.CreateDbContext(connectionString))
        {
            await db.GetService<IMigrator>().MigrateAsync();

            var hashes = await db.RefreshTokens.Select(t => t.TokenHash).ToListAsync();
            Assert.Equal(3, hashes.Count);
            Assert.Contains(TokenService.HashRefreshToken(generated), hashes);
            Assert.Contains(TokenService.HashRefreshToken(FixedToken), hashes);
            Assert.Contains(TokenService.HashRefreshToken(revoked), hashes);
        }

        await using var services = fixture.BuildServices(connectionString);

        foreach (var token in new[] { generated, FixedToken })
        {
            await using var scope = services.CreateAsyncScope();
            var response = await scope.ServiceProvider.GetRequiredService<IAuthService>()
                .RefreshTokenAsync(new RefreshTokenRequest { RefreshToken = token });
            Assert.False(string.IsNullOrEmpty(response.RefreshToken));
        }

        await using (var scope = services.CreateAsyncScope())
        {
            var ex = await Assert.ThrowsAsync<InvalidRefreshTokenException>(() =>
                scope.ServiceProvider.GetRequiredService<IAuthService>()
                    .RefreshTokenAsync(new RefreshTokenRequest { RefreshToken = revoked }));
            Assert.Equal("Refresh token expired or revoked.", ex.Message);
        }
    }

    [Fact]
    public async Task Rolling_back_deletes_the_tokens()
    {
        var connectionString = await fixture.CreateDatabaseAsync(MigrationBefore);
        var userId = await SeedUserAsync(connectionString);
        await SeedPlaintextTokenAsync(connectionString, userId, FixedToken, revokedAt: null);

        await using var db = IntegrationFixture.CreateDbContext(connectionString);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync();
        await migrator.MigrateAsync(MigrationBefore);

        var remaining = await db.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [RefreshTokens]")
            .SingleAsync();
        Assert.Equal(0, remaining);
    }

    private static async Task<string> SeedUserAsync(string connectionString)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = "alice@example.com",
            NormalizedUserName = "ALICE@EXAMPLE.COM",
            Email = "alice@example.com",
            NormalizedEmail = "ALICE@EXAMPLE.COM"
        };

        await using var db = IntegrationFixture.CreateDbContext(connectionString);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return user.Id;
    }

    // Raw SQL, since the current model no longer has the Token column.
    private static async Task SeedPlaintextTokenAsync(
        string connectionString, string userId, string token, DateTimeOffset? revokedAt)
    {
        var now = DateTimeOffset.UtcNow;

        await using var db = IntegrationFixture.CreateDbContext(connectionString);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO [RefreshTokens] ([Id], [Token], [UserId], [CreatedAt], [ExpiresAt], [RevokedAt]) VALUES ({Guid.NewGuid()}, {token}, {userId}, {now}, {now.AddDays(30)}, {revokedAt})");
    }
}
