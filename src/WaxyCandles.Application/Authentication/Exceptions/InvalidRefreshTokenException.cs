namespace WaxyCandles.Application.Authentication.Exceptions;

/// <summary>
/// The refresh token is unknown, expired or revoked.
/// </summary>
public sealed class InvalidRefreshTokenException : Exception
{
    public InvalidRefreshTokenException(string message)
        : base(message)
    {
    }
}
