using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WaxyCandles.Application.Authentication.DTOs;
using WaxyCandles.Application.Authentication.Exceptions;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Infrastructure.Authentication;
using WaxyCandles.Infrastructure.Identity;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Migrations;

/// <summary>
/// Runs the real RefreshTokenFamilies migration: tokens stored before it each
/// become their own family, keep working, and an old revoked token stays a
/// plain 401 (its revocation reason is unknown, so no reuse detection).
/// </summary>
[Collection(IntegrationCollection.Name)]
public class RefreshTokenFamiliesTests(IntegrationFixture fixture)
{
    private const string MigrationBefore = "20261103033456_HashRefreshTokens";

    [Fact]
    public async Task Each_existing_token_becomes_its_own_family_and_still_refreshes()
    {
        var connectionString = await fixture.CreateDatabaseAsync(MigrationBefore);
        var userId = await SeedUserAsync(connectionString);

        var generator = new TokenService(new ConfigurationBuilder().Build());
        var active1 = generator.GenerateRefreshToken();
        var active2 = generator.GenerateRefreshToken();
        var revoked = generator.GenerateRefreshToken();

        var active1Id = await SeedTokenAsync(connectionString, userId, active1, revokedAt: null);
        var active2Id = await SeedTokenAsync(connectionString, userId, active2, revokedAt: null);
        var revokedId = await SeedTokenAsync(connectionString, userId, revoked, revokedAt: DateTimeOffset.UtcNow.AddDays(-1));

        await using (var db = IntegrationFixture.CreateDbContext(connectionString))
        {
            await db.GetService<IMigrator>().MigrateAsync();

            var rows = await db.RefreshTokens.ToListAsync();
            Assert.Equal(3, rows.Count);
            Assert.All(rows, r => Assert.Equal(r.Id, r.FamilyId));
            Assert.All(rows, r => Assert.Null(r.RevokedReason));
            Assert.Equal(new[] { active1Id, active2Id, revokedId }.Order(), rows.Select(r => r.Id).Order());
        }

        await using var services = fixture.BuildServices(connectionString);

        await using (var scope = services.CreateAsyncScope())
        {
            var response = await scope.ServiceProvider.GetRequiredService<IAuthService>()
                .RefreshTokenAsync(new RefreshTokenRequest { RefreshToken = active1 });

            await using var db = IntegrationFixture.CreateDbContext(connectionString);
            var hash = TokenService.HashRefreshToken(response.RefreshToken);
            var rotatedInto = await db.RefreshTokens.SingleAsync(t => t.TokenHash == hash);
            Assert.Equal(active1Id, rotatedInto.FamilyId);
        }

        // A revoked pre-migration token: refused, nothing else revoked.
        await using (var scope = services.CreateAsyncScope())
        {
            await Assert.ThrowsAsync<InvalidRefreshTokenException>(() =>
                scope.ServiceProvider.GetRequiredService<IAuthService>()
                    .RefreshTokenAsync(new RefreshTokenRequest { RefreshToken = revoked }));
        }

        await using (var db = IntegrationFixture.CreateDbContext(connectionString))
        {
            Assert.Null((await db.RefreshTokens.SingleAsync(t => t.Id == active2Id)).RevokedAt);
        }

        // Families are separate: active1's session doesn't affect active2.
        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IAuthService>()
                .RefreshTokenAsync(new RefreshTokenRequest { RefreshToken = active2 });
        }
    }

    [Fact]
    public async Task Rolling_back_keeps_the_tokens()
    {
        var connectionString = await fixture.CreateDatabaseAsync(MigrationBefore);
        var userId = await SeedUserAsync(connectionString);
        await SeedTokenAsync(connectionString, userId, "token", revokedAt: null);

        await using var db = IntegrationFixture.CreateDbContext(connectionString);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync();
        await migrator.MigrateAsync(MigrationBefore);

        var remaining = await db.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [RefreshTokens] WHERE [ReplacedByToken] IS NULL")
            .SingleAsync();
        Assert.Equal(1, remaining);
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

    // Raw SQL in the pre-migration shape (no FamilyId, ReplacedByToken present).
    private static async Task<Guid> SeedTokenAsync(
        string connectionString, string userId, string token, DateTimeOffset? revokedAt)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var hash = TokenService.HashRefreshToken(token);

        await using var db = IntegrationFixture.CreateDbContext(connectionString);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO [RefreshTokens] ([Id], [TokenHash], [UserId], [CreatedAt], [ExpiresAt], [RevokedAt]) VALUES ({id}, {hash}, {userId}, {now}, {now.AddDays(30)}, {revokedAt})");

        return id;
    }
}
