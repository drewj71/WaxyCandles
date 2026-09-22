namespace WaxyCandles.Application.Authentication.DTOs;

public sealed class LogoutRequest
{
    public string RefreshToken { get; init; } = string.Empty;
}