namespace WaxyCandles.Application.Common.Exceptions;

/// <summary>
/// The request is invalid. Mapped to 400 with the per-field errors.
/// </summary>
public sealed class RequestValidationException : Exception
{
    public RequestValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("One or more fields are invalid.")
    {
        Errors = errors;
    }

    /// <summary>Field name (camelCase, as in the JSON request) to its error messages.</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; }
}
