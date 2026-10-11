using WaxyCandles.Domain.Entities;

namespace WaxyCandles.Tests.Domain;

public class RefreshTokenTests
{
    [Fact]
    public void Unexpired_unrevoked_token_is_active()
    {
        var token = new RefreshToken
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        };

        Assert.False(token.IsExpired);
        Assert.False(token.IsRevoked);
        Assert.True(token.IsActive);
    }

    [Fact]
    public void Token_past_its_expiry_is_expired_and_inactive()
    {
        var token = new RefreshToken
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1)
        };

        Assert.True(token.IsExpired);
        Assert.False(token.IsRevoked);
        Assert.False(token.IsActive);
    }

    [Fact]
    public void Token_expiring_exactly_now_counts_as_expired()
    {
        // IsExpired uses >=, so the expiry instant itself is already expired.
        var token = new RefreshToken
        {
            ExpiresAt = DateTimeOffset.UtcNow
        };

        Assert.True(token.IsExpired);
        Assert.False(token.IsActive);
    }

    [Fact]
    public void Revoked_token_is_inactive_even_before_expiry()
    {
        var token = new RefreshToken
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            RevokedAt = DateTimeOffset.UtcNow
        };

        Assert.False(token.IsExpired);
        Assert.True(token.IsRevoked);
        Assert.False(token.IsActive);
    }
}
