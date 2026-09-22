namespace WaxyCandles.Application.Authentication.DTOs;

public sealed class RefreshTokenRequest
{
    public string RefreshToken { get; init; } = string.Empty;
}