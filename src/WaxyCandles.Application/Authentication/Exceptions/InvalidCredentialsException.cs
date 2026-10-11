namespace WaxyCandles.Application.Authentication.Exceptions;

/// <summary>
/// Login failed. Deliberately the same for an unknown email and a wrong password,
/// so callers can't probe which emails are registered.
/// </summary>
public sealed class InvalidCredentialsException : Exception
{
    public InvalidCredentialsException()
        : base("Invalid credentials.")
    {
    }
}
