using Microsoft.AspNetCore.Mvc;
using WaxyCandles.Application.Authentication.DTOs;
using WaxyCandles.Infrastructure.Authentication;

namespace WaxyCandles.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;

    public AuthController(IAuthService authService)
    {
        _authService = authService;
    }


    [HttpPost("register")]
    public async Task<IActionResult> Register(
        RegisterRequest request)
    {
        var response = await _authService.RegisterAsync(request);

        return Ok(response);
    }


    [HttpPost("login")]
    public async Task<IActionResult> Login(
        LoginRequest request)
    {
        var response = await _authService.LoginAsync(request);

        return Ok(response);
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(
    RefreshTokenRequest request)
    {
        var response = await _authService.RefreshTokenAsync(request);

        return Ok(response);
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(
    LogoutRequest request)
    {
        await _authService.LogoutAsync(request);

        return Ok();
    }
}