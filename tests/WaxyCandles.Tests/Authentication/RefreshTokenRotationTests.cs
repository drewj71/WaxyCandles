using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WaxyCandles.Application.Authentication.DTOs;
using WaxyCandles.Application.Authentication.Exceptions;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Infrastructure.Authentication;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Authentication;

public class RefreshTokenRotationTests(IntegrationFixture fixture)
    : DatabaseTestBase(fixture)
{
    private const string Password = "Password123";

    private ServiceProvider _services = default!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _services = Fixture.BuildServices(ConnectionString);
    }

    public override async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Register_persists_an_active_refresh_token_valid_for_30_days()
    {
        var response = await RegisterAsync("alice@example.com");

        var stored = await LoadTokenAsync(response.RefreshToken);

        Assert.True(stored.IsActive);
        Assert.InRange(
            stored.ExpiresAt,
            DateTimeOffset.UtcNow.AddDays(30).AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(30).AddMinutes(1));
        Assert.False(string.IsNullOrEmpty(response.AccessToken));
    }

    [Fact]
    public async Task Only_the_hash_of_the_refresh_token_is_stored()
    {
        var response = await RegisterAsync("alice@example.com");

        await using var db = NewDbContext();
        var stored = await db.RefreshTokens.SingleAsync();

        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(response.RefreshToken)), stored.TokenHash);
        Assert.Equal(32, stored.TokenHash.Length);

        // Nothing in the row, read as raw SQL, holds the token handed to the client.
        var rawColumns = await db.Database
            .SqlQueryRaw<string>(
                "SELECT CONCAT_WS('|', [Id], [UserId], [ReplacedByToken], " +
                "CONVERT(varchar(64), [TokenHash], 2)) AS [Value] FROM [RefreshTokens]")
            .SingleAsync();
        Assert.DoesNotContain(response.RefreshToken, rawColumns);
    }

    [Fact]
    public async Task Each_login_stores_a_distinct_hash()
    {
        var registered = await RegisterAsync("alice@example.com");
        var login = await WithAuthServiceAsync(auth => auth.LoginAsync(
            new LoginRequest { Email = "alice@example.com", Password = Password }));

        Assert.NotEqual(
            (await LoadTokenAsync(registered.RefreshToken)).Id,
            (await LoadTokenAsync(login.RefreshToken)).Id);
    }

    [Fact]
    public async Task Refreshing_rotates_the_token_and_revokes_the_old_one()
    {
        var registered = await RegisterAsync("alice@example.com");

        var refreshed = await RefreshAsync(registered.RefreshToken);

        Assert.NotEqual(registered.RefreshToken, refreshed.RefreshToken);
        Assert.False(string.IsNullOrEmpty(refreshed.AccessToken));

        var oldToken = await LoadTokenAsync(registered.RefreshToken);
        var newToken = await LoadTokenAsync(refreshed.RefreshToken);

        Assert.True(oldToken.IsRevoked);
        Assert.True(newToken.IsActive);
        Assert.Equal(oldToken.UserId, newToken.UserId);
    }

    [Fact]
    public async Task Reusing_an_already_rotated_token_is_rejected()
    {
        var registered = await RegisterAsync("alice@example.com");
        await RefreshAsync(registered.RefreshToken);

        var ex = await Assert.ThrowsAsync<InvalidRefreshTokenException>(
            () => RefreshAsync(registered.RefreshToken));

        Assert.Equal("Refresh token expired or revoked.", ex.Message);
    }

    [Fact]
    public async Task Reusing_a_rotated_token_does_not_revoke_its_replacement()
    {
        // Current behavior: a replayed rotated token is rejected, but the
        // session it was rotated into stays usable (no family revocation).
        var registered = await RegisterAsync("alice@example.com");
        var refreshed = await RefreshAsync(registered.RefreshToken);

        await Assert.ThrowsAsync<InvalidRefreshTokenException>(
            () => RefreshAsync(registered.RefreshToken));

        Assert.True((await LoadTokenAsync(refreshed.RefreshToken)).IsActive);
        await RefreshAsync(refreshed.RefreshToken);
    }

    [Fact]
    public async Task The_stored_hash_is_not_accepted_as_a_refresh_token()
    {
        var registered = await RegisterAsync("alice@example.com");
        var stored = await LoadTokenAsync(registered.RefreshToken);

        var ex = await Assert.ThrowsAsync<InvalidRefreshTokenException>(
            () => RefreshAsync(Convert.ToBase64String(stored.TokenHash)));

        Assert.Equal("Invalid refresh token.", ex.Message);
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        var registered = await RegisterAsync("alice@example.com");

        await using (var db = NewDbContext())
        {
            var hash = TokenService.HashRefreshToken(registered.RefreshToken);
            var stored = await db.RefreshTokens.SingleAsync(t => t.TokenHash == hash);
            stored.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        var ex = await Assert.ThrowsAsync<InvalidRefreshTokenException>(
            () => RefreshAsync(registered.RefreshToken));

        Assert.Equal("Refresh token expired or revoked.", ex.Message);
    }

    [Fact]
    public async Task Unknown_token_is_rejected()
    {
        var ex = await Assert.ThrowsAsync<InvalidRefreshTokenException>(
            () => RefreshAsync("not-a-real-token"));

        Assert.Equal("Invalid refresh token.", ex.Message);
    }

    [Fact]
    public async Task Logout_revokes_the_token_so_it_cannot_be_refreshed()
    {
        var registered = await RegisterAsync("alice@example.com");

        await WithAuthServiceAsync(auth => auth.LogoutAsync(
            new LogoutRequest { RefreshToken = registered.RefreshToken }));

        Assert.True((await LoadTokenAsync(registered.RefreshToken)).IsRevoked);

        await Assert.ThrowsAsync<InvalidRefreshTokenException>(
            () => RefreshAsync(registered.RefreshToken));
    }

    [Fact]
    public async Task Logout_with_an_unknown_token_is_a_no_op()
    {
        await WithAuthServiceAsync(auth => auth.LogoutAsync(
            new LogoutRequest { RefreshToken = "not-a-real-token" }));
    }

    [Fact]
    public async Task Logout_only_revokes_the_given_session()
    {
        var registered = await RegisterAsync("alice@example.com");
        var secondLogin = await WithAuthServiceAsync(auth => auth.LoginAsync(
            new LoginRequest { Email = "alice@example.com", Password = Password }));

        await WithAuthServiceAsync(auth => auth.LogoutAsync(
            new LogoutRequest { RefreshToken = registered.RefreshToken }));

        Assert.True((await LoadTokenAsync(registered.RefreshToken)).IsRevoked);
        Assert.True((await LoadTokenAsync(secondLogin.RefreshToken)).IsActive);
    }

    private Task<AuthResponse> RegisterAsync(string email) =>
        WithAuthServiceAsync(auth => auth.RegisterAsync(
            new RegisterRequest { Email = email, Password = Password, ConfirmPassword = Password }));

    private Task<AuthResponse> RefreshAsync(string refreshToken) =>
        WithAuthServiceAsync(auth => auth.RefreshTokenAsync(
            new RefreshTokenRequest { RefreshToken = refreshToken }));

    // Each call gets its own DI scope, like a separate HTTP request would.
    private async Task<T> WithAuthServiceAsync<T>(Func<IAuthService, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<IAuthService>());
    }

    private async Task WithAuthServiceAsync(Func<IAuthService, Task> action)
    {
        await using var scope = _services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<IAuthService>());
    }

    private async Task<RefreshToken> LoadTokenAsync(string token)
    {
        await using var db = NewDbContext();
        var hash = TokenService.HashRefreshToken(token);
        return await db.RefreshTokens.SingleAsync(t => t.TokenHash == hash);
    }
}
