using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using WaxyCandles.Infrastructure.Identity;

namespace WaxyCandles.Infrastructure.Authentication;

public class TokenService : ITokenService
{
    private readonly IConfiguration _configuration;

    public TokenService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string GenerateAccessToken(ApplicationUser user)
    {
        var jwt = _configuration.GetSection("Jwt");

        var claims = new[]
        {
            new Claim(
                ClaimTypes.NameIdentifier,
                user.Id),

            new Claim(
                ClaimTypes.Email,
                user.Email ?? string.Empty),

            new Claim(
                JwtRegisteredClaimNames.Jti,
                Guid.NewGuid().ToString())
        };

        var key = JwtSigningKey.From(_configuration);

        var credentials = new SigningCredentials(
            key,
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: jwt["Issuer"],
            audience: jwt["Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler()
            .WriteToken(token);
    }


    public string GenerateRefreshToken()
    {
        var randomBytes = RandomNumberGenerator.GetBytes(64);

        return Convert.ToBase64String(randomBytes);
    }

    public const int RefreshTokenHashLength = SHA256.HashSizeInBytes;

    // What is stored and looked up instead of the refresh token itself.
    // A plain SHA-256 is enough: the token is 512 random bits, so it can't
    // be guessed or brute-forced from its hash, and a slow password KDF
    // would only add cost to every refresh. It must stay SHA-256 over the
    // UTF-8 bytes of the token string: the HashRefreshTokens migration
    // hashed existing rows the same way in SQL.
    public static byte[] HashRefreshToken(string refreshToken)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);

        return SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken));
    }
}