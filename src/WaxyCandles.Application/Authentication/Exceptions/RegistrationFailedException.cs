namespace WaxyCandles.Application.Authentication.Exceptions;

/// <summary>
/// The registration request was invalid, e.g. the password doesn't meet the
/// policy or doesn't match its confirmation.
/// </summary>
public sealed class RegistrationFailedException : Exception
{
    public RegistrationFailedException(string message)
        : base(message)
    {
    }
}
