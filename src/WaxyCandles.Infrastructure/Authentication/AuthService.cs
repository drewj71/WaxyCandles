using Microsoft.AspNetCore.Identity;
using WaxyCandles.Application.Authentication.DTOs;
using WaxyCandles.Application.Authentication.Exceptions;
using WaxyCandles.Infrastructure.Identity;
using WaxyCandles.Infrastructure.Persistence;
using WaxyCandles.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace WaxyCandles.Infrastructure.Authentication;

public class AuthService : IAuthService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly ITokenService _tokenService;
    private readonly AppDbContext _dbContext;

    public AuthService(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        ITokenService tokenService,
        AppDbContext dbContext)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _tokenService = tokenService;
        _dbContext = dbContext;
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

        var accessToken = _tokenService.GenerateAccessToken(user);

        var refreshTokenValue = _tokenService.GenerateRefreshToken();

        var refreshToken = new RefreshToken
        {
            Id = Guid.NewGuid(),

            TokenHash = TokenService.HashRefreshToken(refreshTokenValue),

            UserId = user.Id,

            CreatedAt = DateTime.UtcNow,

            ExpiresAt = DateTime.UtcNow.AddDays(30)
        };


        _dbContext.RefreshTokens.Add(refreshToken);

        await _dbContext.SaveChangesAsync();

        return new AuthResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshTokenValue,
            ExpiresAt = DateTime.UtcNow.AddMinutes(15)
        };
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

        var accessToken = _tokenService.GenerateAccessToken(user);

        var refreshTokenValue = _tokenService.GenerateRefreshToken();

        var refreshToken = new RefreshToken
        {
            Id = Guid.NewGuid(),

            TokenHash = TokenService.HashRefreshToken(refreshTokenValue),

            UserId = user.Id,

            CreatedAt = DateTime.UtcNow,

            ExpiresAt = DateTime.UtcNow.AddDays(30)
        };

        _dbContext.RefreshTokens.Add(refreshToken);

        await _dbContext.SaveChangesAsync();

        return new AuthResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshTokenValue,
            ExpiresAt = DateTime.UtcNow.AddMinutes(15)
        };
    }

    public async Task<AuthResponse> RefreshTokenAsync(
    RefreshTokenRequest request)
    {
        var tokenHash = TokenService.HashRefreshToken(request.RefreshToken);

        var storedToken = await _dbContext.RefreshTokens
            .FirstOrDefaultAsync(x => x.TokenHash == tokenHash) ?? throw new InvalidRefreshTokenException("Invalid refresh token.");

        if (!storedToken.IsActive)
        {
            throw new InvalidRefreshTokenException("Refresh token expired or revoked.");
        }

        var user = await _userManager.FindByIdAsync(
            storedToken.UserId) ?? throw new InvalidRefreshTokenException("User not found.");

        // Rotate refresh token
        storedToken.RevokedAt = DateTime.UtcNow;

        var newRefreshTokenValue = _tokenService.GenerateRefreshToken();

        var newRefreshToken = new RefreshToken
        {
            Id = Guid.NewGuid(),

            TokenHash = TokenService.HashRefreshToken(newRefreshTokenValue),

            UserId = user.Id,

            CreatedAt = DateTime.UtcNow,

            ExpiresAt = DateTime.UtcNow.AddDays(30)
        };

        _dbContext.RefreshTokens.Add(newRefreshToken);

        var accessToken =
            _tokenService.GenerateAccessToken(user);

        await _dbContext.SaveChangesAsync();

        return new AuthResponse
        {
            AccessToken = accessToken,

            RefreshToken = newRefreshTokenValue,

            ExpiresAt = DateTime.UtcNow.AddMinutes(15)
        };
    }

    public async Task LogoutAsync(LogoutRequest request)
    {
        var tokenHash = TokenService.HashRefreshToken(request.RefreshToken);

        var refreshToken = await _dbContext.RefreshTokens
            .FirstOrDefaultAsync(x => x.TokenHash == tokenHash);

        if (refreshToken == null)
        {
            return;
        }

        refreshToken.RevokedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();
    }
}