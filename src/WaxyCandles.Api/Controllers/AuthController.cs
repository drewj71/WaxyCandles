using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WaxyCandles.Application.Authentication.DTOs;
using WaxyCandles.Infrastructure.Authentication;

namespace WaxyCandles.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private const string AccessTokenCookie = "waxy_access_token";
    private const string RefreshTokenCookie = "waxy_refresh_token";

    private readonly IAuthService _authService;

    public AuthController(IAuthService authService)
    {
        _authService = authService;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register(
        RegisterRequest request)
    {
        var response =
            await _authService.RegisterAsync(request);

        SetAuthCookies(response);

        return Ok();
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login(
        LoginRequest request)
    {
        var response =
            await _authService.LoginAsync(request);

        SetAuthCookies(response);

        return Ok();
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh()
    {
        if (!Request.Cookies.TryGetValue(
                RefreshTokenCookie,
                out var refreshToken))
        {
            return Unauthorized();
        }

        var response =
            await _authService.RefreshTokenAsync(
                new RefreshTokenRequest
                {
                    RefreshToken = refreshToken
                });

        SetAuthCookies(response);

        return Ok();
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        if (Request.Cookies.TryGetValue(
                RefreshTokenCookie,
                out var refreshToken))
        {
            await _authService.LogoutAsync(
                new LogoutRequest
                {
                    RefreshToken = refreshToken
                });
        }

        Response.Cookies.Delete(AccessTokenCookie);
        Response.Cookies.Delete(RefreshTokenCookie);

        return Ok();
    }

    [Authorize]
    [HttpGet("me")]
    public IActionResult Me()
    {
        return Ok(new
        {
            Id = User.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            Email = User.FindFirst(ClaimTypes.Email)?.Value
        });
    }

    private void SetAuthCookies(AuthResponse response)
    {
        Response.Cookies.Append(
            AccessTokenCookie,
            response.AccessToken,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Expires = response.ExpiresAt
            });

        Response.Cookies.Append(
            RefreshTokenCookie,
            response.RefreshToken,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Expires =
                    DateTimeOffset.UtcNow.AddDays(30)
            });
    }
}