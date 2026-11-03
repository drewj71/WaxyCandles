using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using WaxyCandles.Infrastructure.Authentication;
using WaxyCandles.Infrastructure.Identity;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Authentication;

public class TokenServiceTests
{
    private readonly TokenService _tokenService = new(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = IntegrationFixture.TestJwtSecret,
                ["Jwt:Issuer"] = IntegrationFixture.TestJwtIssuer,
                ["Jwt:Audience"] = IntegrationFixture.TestJwtAudience
            })
            .Build());

    private readonly ApplicationUser _user = new()
    {
        Id = "user-123",
        Email = "alice@example.com"
    };

    [Fact]
    public void Access_token_carries_user_id_email_and_a_unique_jti()
    {
        var first = Validate(_tokenService.GenerateAccessToken(_user));
        var second = Validate(_tokenService.GenerateAccessToken(_user));

        Assert.Equal("user-123", first.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("alice@example.com", first.FindFirstValue(ClaimTypes.Email));
        Assert.NotEqual(
            first.FindFirstValue(JwtRegisteredClaimNames.Jti),
            second.FindFirstValue(JwtRegisteredClaimNames.Jti));
    }

    [Fact]
    public void Access_token_has_configured_issuer_audience_and_15_minute_lifetime()
    {
        var before = DateTime.UtcNow;
        var token = new JwtSecurityTokenHandler().ReadJwtToken(
            _tokenService.GenerateAccessToken(_user));

        Assert.Equal(IntegrationFixture.TestJwtIssuer, token.Issuer);
        Assert.Equal([IntegrationFixture.TestJwtAudience], token.Audiences);
        Assert.Equal(SecurityAlgorithms.HmacSha256, token.Header.Alg);

        // JWT exp has whole-second precision.
        Assert.InRange(
            token.ValidTo,
            before.AddMinutes(15).AddSeconds(-1),
            DateTime.UtcNow.AddMinutes(15).AddSeconds(1));
    }

    [Fact]
    public void Access_token_does_not_validate_against_a_different_key()
    {
        var token = _tokenService.GenerateAccessToken(_user);

        Assert.ThrowsAny<SecurityTokenException>(() =>
            Validate(token, "a-completely-different-key-that-is-long-enough!!"));
    }

    [Fact]
    public void User_without_email_gets_an_empty_email_claim()
    {
        var principal = Validate(_tokenService.GenerateAccessToken(
            new ApplicationUser { Id = "user-123", Email = null }));

        Assert.Equal(string.Empty, principal.FindFirstValue(ClaimTypes.Email));
    }

    [Fact]
    public void Refresh_token_is_64_random_bytes_base64_encoded()
    {
        var tokens = Enumerable.Range(0, 100)
            .Select(_ => _tokenService.GenerateRefreshToken())
            .ToList();

        Assert.All(tokens, t => Assert.Equal(64, Convert.FromBase64String(t).Length));
        Assert.Equal(tokens.Count, tokens.Distinct().Count());
    }

    [Fact]
    public void Refresh_token_hash_is_sha256_of_the_utf8_token()
    {
        // FIPS 180-2 test vector for "abc". The HashRefreshTokens migration
        // hashes existing rows with HASHBYTES('SHA2_256', varchar), so this
        // must stay plain SHA-256 over the token's (ASCII) bytes.
        Assert.Equal(
            "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD",
            Convert.ToHexString(TokenService.HashRefreshToken("abc")));
    }

    [Fact]
    public void Refresh_token_hash_is_fixed_length_and_deterministic()
    {
        var token = _tokenService.GenerateRefreshToken();

        var hash = TokenService.HashRefreshToken(token);

        Assert.Equal(TokenService.RefreshTokenHashLength, hash.Length);
        Assert.Equal(32, hash.Length);
        Assert.Equal(hash, TokenService.HashRefreshToken(token));
        Assert.NotEqual(hash, TokenService.HashRefreshToken(_tokenService.GenerateRefreshToken()));
    }

    private static ClaimsPrincipal Validate(
        string token,
        string signingKey = IntegrationFixture.TestJwtSecret) =>
        new JwtSecurityTokenHandler().ValidateToken(
            token,
            new TokenValidationParameters
            {
                ValidIssuer = IntegrationFixture.TestJwtIssuer,
                ValidAudience = IntegrationFixture.TestJwtAudience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                ClockSkew = TimeSpan.Zero
            },
            out _);
}
