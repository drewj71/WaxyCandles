using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace WaxyCandles.Infrastructure.Authentication;

/// <summary>The HS256 key from <c>Jwt:Secret</c>, shared by token issuing and validation.</summary>
public static class JwtSigningKey
{
    public const string ConfigKey = "Jwt:Secret";

    /// <summary>HS256 needs a key of at least 256 bits.</summary>
    public const int MinimumBytes = 32;

    /// <exception cref="InvalidOperationException">
    /// <c>Jwt:Secret</c> is missing or too short. The message names the key
    /// but never includes its value.
    /// </exception>
    public static SymmetricSecurityKey From(IConfiguration configuration)
    {
        var secret = configuration[ConfigKey];

        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new InvalidOperationException(
                $"{ConfigKey} is not set. Set it with dotnet user-secrets in " +
                "Development, or the Jwt__Secret environment variable.");
        }

        var bytes = Encoding.UTF8.GetBytes(secret);

        if (bytes.Length < MinimumBytes)
        {
            throw new InvalidOperationException(
                $"{ConfigKey} is too short: HS256 needs at least {MinimumBytes} " +
                $"bytes (256 bits), but it has {bytes.Length}.");
        }

        return new SymmetricSecurityKey(bytes);
    }
}
