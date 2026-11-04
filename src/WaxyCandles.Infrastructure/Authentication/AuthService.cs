using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WaxyCandles.Application.Authentication.DTOs;
using WaxyCandles.Application.Authentication.Exceptions;
using WaxyCandles.Domain.Entities;
using WaxyCandles.Infrastructure.Identity;
using WaxyCandles.Infrastructure.Persistence;

namespace WaxyCandles.Infrastructure.Authentication;

public class AuthService : IAuthService
{
    public static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(30);

    /// <summary>
    /// How long after a rotation the rotated token may be presented again
    /// without counting as reuse. Covers a legitimate concurrent refresh: a
    /// second tab (or a retried request) sent the same cookie before the
    /// winner's Set-Cookie arrived. Such a request still gets a 401 (it is
    /// never given a token), and the web client then retries with the new
    /// cookie. Outside the window a replay revokes the whole family.
    /// </summary>
    public static readonly TimeSpan RotationGracePeriod = TimeSpan.FromSeconds(10);

    private const string RevokedMessage = "Refresh token expired or revoked.";
    private const string InvalidMessage = "Invalid refresh token.";

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly ITokenService _tokenService;
    private readonly AppDbContext _dbContext;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        ITokenService tokenService,
        AppDbContext dbContext,
        ILogger<AuthService> logger)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _tokenService = tokenService;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request)
    {
        if (request.Password != request.ConfirmPassword)
        {
            throw new RegistrationFailedException("Passwords do not match.");
        }

        var existingUser = await _userManager.FindByEmailAsync(request.Email);

        if (existingUser != null)
        {
            throw new UserAlreadyExistsException();
        }

        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email
        };

        var result = await _userManager.CreateAsync(
            user,
            request.Password);

        if (!result.Succeeded)
        {
            var errors = string.Join(
                ", ",
                result.Errors.Select(x => x.Description));

            throw new RegistrationFailedException(errors);
        }

        return await StartSessionAsync(user);
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request)
    {
        var user = await _userManager.FindByEmailAsync(request.Email) ?? throw new InvalidCredentialsException();

        // Counts failures towards Identity's lockout (IdentityOptions.Lockout),
        // and refuses even the right password while the account is locked out.
        // Locked out, wrong password and unknown email all get the same 401,
        // so the response never reveals which it was.
        var result = await _signInManager.CheckPasswordSignInAsync(
            user,
            request.Password,
            lockoutOnFailure: true);

        if (!result.Succeeded)
        {
            throw new InvalidCredentialsException();
        }

        return await StartSessionAsync(user);
    }

    public async Task<AuthResponse> RefreshTokenAsync(
        RefreshTokenRequest request)
    {
        var tokenHash = TokenService.HashRefreshToken(request.RefreshToken);

        // Made once, outside the execution strategy, so a replayed attempt can
        // tell whether an earlier attempt's commit went through.
        var newTokenValue = _tokenService.GenerateRefreshToken();
        var newTokenId = Guid.NewGuid();

        var strategy = _dbContext.Database.CreateExecutionStrategy();

        // Replay-safe: every attempt starts from a clean change tracker and
        // re-reads the token. The rotation itself is conditional, so two
        // requests with the same token can't both rotate it.
        var outcome = await strategy.ExecuteAsync(async () =>
        {
            _dbContext.ChangeTracker.Clear();

            // An earlier attempt committed but reported an error (ambiguous
            // commit): the rotation is done, so finish it.
            var committedUserId = await _dbContext.RefreshTokens
                .Where(x => x.Id == newTokenId)
                .Select(x => x.UserId)
                .FirstOrDefaultAsync();

            if (committedUserId is not null)
            {
                return RotationOutcome.Rotated(committedUserId);
            }

            var stored = await FindByHashAsync(tokenHash);

            if (stored is null || !stored.IsActive)
            {
                return RotationOutcome.Rejected(stored);
            }

            await using var transaction = await _dbContext.Database.BeginTransactionAsync();

            var now = DateTimeOffset.UtcNow;

            // A concurrent refresh with the same token blocks on this row's
            // lock and then matches nothing, so exactly one request wins.
            var rotated = await _dbContext.RefreshTokens
                .Where(x => x.Id == stored.Id && x.RevokedAt == null && x.ExpiresAt > now)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.RevokedAt, now)
                    .SetProperty(x => x.RevokedReason, RefreshTokenRevocationReason.Rotated));

            if (rotated == 0)
            {
                await transaction.RollbackAsync();

                // Lost the race (or it just expired): judge the token as it is now.
                return RotationOutcome.Rejected(await FindByHashAsync(tokenHash));
            }

            _dbContext.RefreshTokens.Add(new RefreshToken
            {
                Id = newTokenId,
                TokenHash = TokenService.HashRefreshToken(newTokenValue),
                UserId = stored.UserId,
                FamilyId = stored.FamilyId,
                CreatedAt = now,
                ExpiresAt = now + RefreshTokenLifetime
            });

            await _dbContext.SaveChangesAsync();
            await transaction.CommitAsync();

            return RotationOutcome.Rotated(stored.UserId);
        });

        if (outcome.UserId is null)
        {
            await RejectAsync(outcome.Token);
        }

        var user = await _userManager.FindByIdAsync(outcome.UserId!)
            ?? throw new InvalidRefreshTokenException("User not found.");

        return new AuthResponse
        {
            AccessToken = _tokenService.GenerateAccessToken(user),
            RefreshToken = newTokenValue,
            ExpiresAt = DateTime.UtcNow.AddMinutes(15)
        };
    }

    public async Task LogoutAsync(LogoutRequest request)
    {
        var tokenHash = TokenService.HashRefreshToken(request.RefreshToken);

        var refreshToken = await FindByHashAsync(tokenHash);

        if (refreshToken == null)
        {
            return;
        }

        // Ends the session, not just this token: the family's live token is
        // revoked even if a stale tab logs out with an already-rotated one.
        // Never counts as reuse. Already-revoked rows keep their reason.
        await RevokeFamilyAsync(refreshToken.FamilyId, RefreshTokenRevocationReason.Logout);
    }

    private async Task<AuthResponse> StartSessionAsync(ApplicationUser user)
    {
        var refreshTokenValue = _tokenService.GenerateRefreshToken();
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();

        _dbContext.RefreshTokens.Add(new RefreshToken
        {
            Id = id,
            TokenHash = TokenService.HashRefreshToken(refreshTokenValue),
            UserId = user.Id,
            // A new family per sign-in, so logging out (or reuse) on one
            // device doesn't end the user's other sessions.
            FamilyId = Guid.NewGuid(),
            CreatedAt = now,
            ExpiresAt = now + RefreshTokenLifetime
        });

        await _dbContext.SaveChangesVerifiedAsync(
            ct => _dbContext.RefreshTokens.AnyAsync(x => x.Id == id, ct));

        return new AuthResponse
        {
            AccessToken = _tokenService.GenerateAccessToken(user),
            RefreshToken = refreshTokenValue,
            ExpiresAt = DateTime.UtcNow.AddMinutes(15)
        };
    }

    /// <summary>Always throws <see cref="InvalidRefreshTokenException"/>.</summary>
    private async Task RejectAsync(RefreshToken? token)
    {
        if (token is null)
        {
            throw new InvalidRefreshTokenException(InvalidMessage);
        }

        // Checked before expiry on purpose: an old rotated token is still
        // evidence that a copy exists, and its family's newest token may
        // still be live. Rows are kept past expiry (Retention) for this.
        if (token.RevokedReason == RefreshTokenRevocationReason.Rotated)
        {
            if (DateTimeOffset.UtcNow - token.RevokedAt <= RotationGracePeriod)
            {
                _logger.LogInformation(
                    "Refresh token family {FamilyId} of user {UserId}: a token rotated moments ago was presented again; treated as a concurrent refresh.",
                    token.FamilyId,
                    token.UserId);
            }
            else
            {
                var revoked = await RevokeFamilyAsync(
                    token.FamilyId,
                    RefreshTokenRevocationReason.ReuseDetected);

                _logger.LogWarning(
                    "Refresh token reuse detected for user {UserId}: a token rotated at {RotatedAt:u} was presented again. Revoked {Count} live token(s) of family {FamilyId}.",
                    token.UserId,
                    token.RevokedAt,
                    revoked,
                    token.FamilyId);
            }
        }

        throw new InvalidRefreshTokenException(RevokedMessage);
    }

    // One statement, so no explicit transaction; EF's retry may replay it
    // safely because it only touches rows that aren't revoked yet.
    private Task<int> RevokeFamilyAsync(Guid familyId, RefreshTokenRevocationReason reason)
    {
        var now = DateTimeOffset.UtcNow;

        return _dbContext.RefreshTokens
            .Where(x => x.FamilyId == familyId && x.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.RevokedAt, now)
                .SetProperty(x => x.RevokedReason, reason));
    }

    private Task<RefreshToken?> FindByHashAsync(byte[] tokenHash) =>
        _dbContext.RefreshTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TokenHash == tokenHash);

    /// <summary>
    /// Either the token was rotated (UserId set) or it was refused, with the
    /// stored row (null if unknown) to decide how.
    /// </summary>
    private readonly record struct RotationOutcome(string? UserId, RefreshToken? Token)
    {
        public static RotationOutcome Rotated(string userId) => new(userId, null);

        public static RotationOutcome Rejected(RefreshToken? token) => new(null, token);
    }
}
