namespace WaxyCandles.Application.Authentication.Exceptions;

public sealed class UserAlreadyExistsException : Exception
{
    public UserAlreadyExistsException()
        : base("User already exists.")
    {
    }
}
