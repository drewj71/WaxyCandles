using Microsoft.AspNetCore.Identity;
using WaxyCandles.Application.Authentication.DTOs;
using WaxyCandles.Infrastructure.Identity;
using WaxyCandles.Infrastructure.Persistence;
using WaxyCandles.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace WaxyCandles.Infrastructure.Authentication;

public class AuthService : IAuthService
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ITokenService _tokenService;
    private readonly AppDbContext _dbContext;

    public AuthService(
        UserManager<ApplicationUser> userManager,
        ITokenService tokenService,
        AppDbContext dbContext)
    {
        _userManager = userManager;
        _tokenService = tokenService;
        _dbContext = dbContext;
    }

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request)
    {
        var existingUser = await _userManager.FindByEmailAsync(request.Email);

        if (existingUser != null)
        {
            throw new Exception("User already exists.");
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

            throw new Exception(errors);
        }

        var accessToken = _tokenService.GenerateAccessToken(user);

        var refreshToken = new RefreshToken
        {
            Id = Guid.NewGuid(),

            Token = _tokenService.GenerateRefreshToken(),

            UserId = user.Id,

            CreatedAt = DateTime.UtcNow,

            ExpiresAt = DateTime.UtcNow.AddDays(30)
        };


        _dbContext.RefreshTokens.Add(refreshToken);

        await _dbContext.SaveChangesAsync();

        return new AuthResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken.Token,
            ExpiresAt = DateTime.UtcNow.AddMinutes(15)
        };
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request)
    {
        var user = await _userManager.FindByEmailAsync(request.Email) ?? throw new Exception("Invalid credentials.");
        var passwordValid = await _userManager.CheckPasswordAsync(
            user,
            request.Password);

        if (!passwordValid)
        {
            throw new Exception("Invalid credentials.");
        }

        var accessToken = _tokenService.GenerateAccessToken(user);

        var refreshToken = new RefreshToken
        {
            Id = Guid.NewGuid(),

            Token = _tokenService.GenerateRefreshToken(),

            UserId = user.Id,

            CreatedAt = DateTime.UtcNow,

            ExpiresAt = DateTime.UtcNow.AddDays(30)
        };

        _dbContext.RefreshTokens.Add(refreshToken);

        await _dbContext.SaveChangesAsync();

        return new AuthResponse
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken.Token,
            ExpiresAt = DateTime.UtcNow.AddMinutes(15)
        };
    }

    public async Task<AuthResponse> RefreshTokenAsync(
    RefreshTokenRequest request)
    {
        var storedToken = await _dbContext.RefreshTokens
            .FirstOrDefaultAsync(x => x.Token == request.RefreshToken) ?? throw new Exception("Invalid refresh token.");

        if (!storedToken.IsActive)
        {
            throw new Exception("Refresh token expired or revoked.");
        }

        var user = await _userManager.FindByIdAsync(
            storedToken.UserId) ?? throw new Exception("User not found.");

        // Rotate refresh token
        storedToken.RevokedAt = DateTime.UtcNow;

        var newRefreshToken = new RefreshToken
        {
            Id = Guid.NewGuid(),

            Token = _tokenService.GenerateRefreshToken(),

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

            RefreshToken = newRefreshToken.Token,

            ExpiresAt = DateTime.UtcNow.AddMinutes(15)
        };
    }

    public async Task LogoutAsync(LogoutRequest request)
    {
        var refreshToken = await _dbContext.RefreshTokens
            .FirstOrDefaultAsync(x => x.Token == request.RefreshToken);

        if (refreshToken == null)
        {
            return;
        }

        refreshToken.RevokedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();
    }
}