using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WaxyCandles.Application.Authentication.DTOs;
using WaxyCandles.Application.Authentication.Exceptions;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Infrastructure.Authentication;
using WaxyCandles.Tests.Infrastructure;
using WaxyCandles.Tests.Resilience;

namespace WaxyCandles.Tests.Authentication;

/// <summary>
/// Token families: replaying a rotated token (outside the short grace window)
/// revokes every token from the same sign-in; a logged-out token is just
/// refused; concurrent refreshes with one token have exactly one winner.
/// </summary>
public class RefreshTokenReuseDetectionTests(IntegrationFixture fixture)
    : DatabaseTestBase(fixture)
{
    private const string Password = "Password123";

    private readonly CapturingLoggerProvider _logs = new();
    private readonly CommitFault _commitFault = new(afterCommit: true) { Armed = false };
    private readonly CommitFault _commitFaultBefore = new(afterCommit: false) { Armed = false };
    private readonly ReadBarrier _readBarrier = new();
    private ServiceProvider _services = default!;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _services = Fixture.BuildServices(
            ConnectionString,
            services => services.AddDbInterceptors(_commitFault, _commitFaultBefore, _readBarrier),
            _logs);
    }

    /// <summary>
    /// Once armed, holds the first <c>participants</c> reads of a refresh
    /// token by hash until all of them have read, then lets every later
    /// command through.
    /// </summary>
    private sealed class ReadBarrier : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        private CountdownEvent? _countdown;
        private int _slots;

        public bool Released { get; private set; }

        public void Arm(int participants)
        {
            _countdown = new CountdownEvent(participants);
            _slots = participants;
        }

        public override async ValueTask<System.Data.Common.DbDataReader> ReaderExecutedAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandExecutedEventData eventData,
            System.Data.Common.DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            var countdown = _countdown;

            if (countdown is not null &&
                command.CommandText.Contains("[TokenHash] =", StringComparison.Ordinal) &&
                Interlocked.Decrement(ref _slots) >= 0)
            {
                countdown.Signal();
                Released = await Task.Run(() => countdown.Wait(TimeSpan.FromSeconds(15)), cancellationToken);
            }

            return result;
        }
    }

    public override async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Rotation_keeps_the_family_and_each_sign_in_starts_a_new_one()
    {
        var registered = await RegisterAsync("alice@example.com");
        var first = await RefreshAsync(registered.RefreshToken);
        var second = await RefreshAsync(first.RefreshToken);
        var login = await LoginAsync("alice@example.com");

        var root = await LoadTokenAsync(registered.RefreshToken);

        Assert.Equal(root.FamilyId, (await LoadTokenAsync(first.RefreshToken)).FamilyId);
        Assert.Equal(root.FamilyId, (await LoadTokenAsync(second.RefreshToken)).FamilyId);
        Assert.NotEqual(root.FamilyId, (await LoadTokenAsync(login.RefreshToken)).FamilyId);

        Assert.Equal(RefreshTokenRevocationReason.Rotated, root.RevokedReason);
        Assert.Equal(RefreshTokenRevocationReason.Rotated, (await LoadTokenAsync(first.RefreshToken)).RevokedReason);
        Assert.Null((await LoadTokenAsync(second.RefreshToken)).RevokedReason);
    }

    [Fact]
    public async Task Replaying_a_rotated_token_revokes_the_family_and_the_newest_token_then_fails()
    {
        var registered = await RegisterAsync("alice@example.com");
        var first = await RefreshAsync(registered.RefreshToken);
        var newest = await RefreshAsync(first.RefreshToken);

        // Another device's session belongs to another family and survives.
        var otherDevice = await LoginAsync("alice@example.com");

        await AgeRotationPastGraceAsync(registered.RefreshToken);

        var ex = await Assert.ThrowsAsync<InvalidRefreshTokenException>(
            () => RefreshAsync(registered.RefreshToken));
        Assert.Equal("Refresh token expired or revoked.", ex.Message);

        var newestRow = await LoadTokenAsync(newest.RefreshToken);
        Assert.True(newestRow.IsRevoked);
        Assert.Equal(RefreshTokenRevocationReason.ReuseDetected, newestRow.RevokedReason);

        // Already-rotated rows keep their reason.
        Assert.Equal(RefreshTokenRevocationReason.Rotated, (await LoadTokenAsync(first.RefreshToken)).RevokedReason);

        await Assert.ThrowsAsync<InvalidRefreshTokenException>(() => RefreshAsync(newest.RefreshToken));

        Assert.True((await LoadTokenAsync(otherDevice.RefreshToken)).IsActive);
        await RefreshAsync(otherDevice.RefreshToken);

        Assert.True(_logs.Contains("Refresh token reuse detected"));
        Assert.DoesNotContain(_logs.Messages, m =>
            m.Contains(registered.RefreshToken) || m.Contains(newest.RefreshToken));
    }

    [Fact]
    public async Task Replaying_a_rotated_token_after_it_expired_still_revokes_the_family()
    {
        var registered = await RegisterAsync("alice@example.com");
        var newest = await RefreshAsync(registered.RefreshToken);

        await UpdateTokenAsync(registered.RefreshToken, t =>
        {
            t.RevokedAt = DateTimeOffset.UtcNow.AddDays(-31);
            t.ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1);
        });

        await Assert.ThrowsAsync<InvalidRefreshTokenException>(() => RefreshAsync(registered.RefreshToken));

        Assert.Equal(
            RefreshTokenRevocationReason.ReuseDetected,
            (await LoadTokenAsync(newest.RefreshToken)).RevokedReason);
    }

    [Fact]
    public async Task Replaying_a_rotated_token_within_the_grace_window_is_refused_but_keeps_the_session()
    {
        var registered = await RegisterAsync("alice@example.com");
        var refreshed = await RefreshAsync(registered.RefreshToken);

        // Immediately after the rotation, as a second tab would.
        await Assert.ThrowsAsync<InvalidRefreshTokenException>(() => RefreshAsync(registered.RefreshToken));

        Assert.True((await LoadTokenAsync(refreshed.RefreshToken)).IsActive);
        Assert.Equal(2, await CountFamilyAsync(registered.RefreshToken));
        await RefreshAsync(refreshed.RefreshToken);
    }

    [Fact]
    public async Task Replaying_a_logged_out_token_is_refused_without_touching_other_sessions()
    {
        var registered = await RegisterAsync("alice@example.com");
        var otherDevice = await LoginAsync("alice@example.com");

        await LogoutAsync(registered.RefreshToken);

        var loggedOut = await LoadTokenAsync(registered.RefreshToken);
        Assert.Equal(RefreshTokenRevocationReason.Logout, loggedOut.RevokedReason);

        // Long after logout too: never reuse detection.
        await UpdateTokenAsync(registered.RefreshToken, t => t.RevokedAt = DateTimeOffset.UtcNow.AddHours(-1));

        var ex = await Assert.ThrowsAsync<InvalidRefreshTokenException>(
            () => RefreshAsync(registered.RefreshToken));
        Assert.Equal("Refresh token expired or revoked.", ex.Message);

        Assert.Equal(RefreshTokenRevocationReason.Logout, (await LoadTokenAsync(registered.RefreshToken)).RevokedReason);
        Assert.True((await LoadTokenAsync(otherDevice.RefreshToken)).IsActive);
        Assert.False(_logs.Contains("Refresh token reuse detected"));
    }

    [Fact]
    public async Task Logout_with_a_stale_rotated_token_ends_the_session_without_reuse_detection()
    {
        var registered = await RegisterAsync("alice@example.com");
        var refreshed = await RefreshAsync(registered.RefreshToken);

        await LogoutAsync(registered.RefreshToken);

        var live = await LoadTokenAsync(refreshed.RefreshToken);
        Assert.True(live.IsRevoked);
        Assert.Equal(RefreshTokenRevocationReason.Logout, live.RevokedReason);
        Assert.Equal(RefreshTokenRevocationReason.Rotated, (await LoadTokenAsync(registered.RefreshToken)).RevokedReason);
        Assert.False(_logs.Contains("Refresh token reuse detected"));
    }

    [Fact]
    public async Task Concurrent_refreshes_with_one_token_have_exactly_one_winner()
    {
        const int attempts = 10;

        var registered = await RegisterAsync("alice@example.com");

        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, attempts)
            .Select(_ => Task.Run(async () =>
            {
                start.Wait();
                try
                {
                    return (Response: await RefreshAsync(registered.RefreshToken), Error: (Exception?)null);
                }
                catch (Exception ex)
                {
                    return (Response: (AuthResponse?)null, Error: ex);
                }
            }))
            .ToList();

        start.Set();
        var results = await Task.WhenAll(tasks);

        var winners = results.Where(r => r.Response is not null).ToList();
        Assert.Single(winners);
        Assert.All(
            results.Where(r => r.Response is null),
            r => Assert.IsType<InvalidRefreshTokenException>(r.Error));

        // One replacement, still live: the losers were inside the grace window.
        Assert.Equal(2, await CountFamilyAsync(registered.RefreshToken));
        var winner = await LoadTokenAsync(winners[0].Response!.RefreshToken);
        Assert.True(winner.IsActive);
        Assert.False(_logs.Contains("Refresh token reuse detected"));

        await RefreshAsync(winners[0].Response!.RefreshToken);
    }

    [Fact]
    public async Task Two_refreshes_that_both_read_the_token_as_active_still_have_one_winner()
    {
        var registered = await RegisterAsync("alice@example.com");

        // Both requests read the token as active before either rotates it, so
        // only the conditional UPDATE can stop the second one.
        _readBarrier.Arm(participants: 2);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
            {
                try
                {
                    return (Response: await RefreshAsync(registered.RefreshToken), Error: (Exception?)null);
                }
                catch (Exception ex)
                {
                    return (Response: (AuthResponse?)null, Error: ex);
                }
            })));

        Assert.True(_readBarrier.Released, "both requests should have read the token before either rotated it");
        Assert.Single(results, r => r.Response is not null);
        Assert.IsType<InvalidRefreshTokenException>(Assert.Single(results, r => r.Response is null).Error);
        Assert.Equal(2, await CountFamilyAsync(registered.RefreshToken));
        Assert.False(_logs.Contains("Refresh token reuse detected"));
    }

    [Theory]
    [InlineData(true)]  // committed, but the client saw an error
    [InlineData(false)] // never committed: rolled back and retried
    public async Task A_transient_commit_failure_still_rotates_exactly_once(bool afterCommit)
    {
        var registered = await RegisterAsync("alice@example.com");

        var fault = afterCommit ? _commitFault : _commitFaultBefore;
        fault.Armed = true;
        var refreshed = await RefreshAsync(registered.RefreshToken);
        fault.Armed = false;

        Assert.Equal(1, fault.Thrown);
        Assert.Equal(2, await CountFamilyAsync(registered.RefreshToken));
        Assert.True((await LoadTokenAsync(refreshed.RefreshToken)).IsActive);
        Assert.Equal(RefreshTokenRevocationReason.Rotated, (await LoadTokenAsync(registered.RefreshToken)).RevokedReason);

        await RefreshAsync(refreshed.RefreshToken);
    }

    private async Task AgeRotationPastGraceAsync(string token) =>
        await UpdateTokenAsync(token, t =>
            t.RevokedAt = DateTimeOffset.UtcNow - AuthService.RotationGracePeriod - TimeSpan.FromSeconds(5));

    private Task<AuthResponse> RegisterAsync(string email) =>
        WithAuthServiceAsync(auth => auth.RegisterAsync(
            new RegisterRequest { Email = email, Password = Password, ConfirmPassword = Password }));

    private Task<AuthResponse> LoginAsync(string email) =>
        WithAuthServiceAsync(auth => auth.LoginAsync(
            new LoginRequest { Email = email, Password = Password }));

    private Task<AuthResponse> RefreshAsync(string refreshToken) =>
        WithAuthServiceAsync(auth => auth.RefreshTokenAsync(
            new RefreshTokenRequest { RefreshToken = refreshToken }));

    private async Task LogoutAsync(string refreshToken)
    {
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IAuthService>()
            .LogoutAsync(new LogoutRequest { RefreshToken = refreshToken });
    }

    // Each call gets its own DI scope, like a separate HTTP request would.
    private async Task<T> WithAuthServiceAsync<T>(Func<IAuthService, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<IAuthService>());
    }

    private async Task<RefreshToken> LoadTokenAsync(string token)
    {
        await using var db = NewDbContext();
        var hash = TokenService.HashRefreshToken(token);
        return await db.RefreshTokens.SingleAsync(t => t.TokenHash == hash);
    }

    private async Task<int> CountFamilyAsync(string token)
    {
        var familyId = (await LoadTokenAsync(token)).FamilyId;
        await using var db = NewDbContext();
        return await db.RefreshTokens.CountAsync(t => t.FamilyId == familyId);
    }

    private async Task UpdateTokenAsync(string token, Action<RefreshToken> update)
    {
        await using var db = NewDbContext();
        var hash = TokenService.HashRefreshToken(token);
        var stored = await db.RefreshTokens.SingleAsync(t => t.TokenHash == hash);
        update(stored);
        await db.SaveChangesAsync();
    }
}
