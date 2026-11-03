namespace WaxyCandles.Domain.Entities;

public class RefreshToken
{
    public Guid Id { get; set; }

    // SHA-256 of the token handed to the client (TokenService.HashRefreshToken).
    // The plaintext is never stored, so a database leak gives no usable session.
    public byte[] TokenHash { get; set; } = [];

    public string UserId { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public string? ReplacedByToken { get; set; }

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;

    public bool IsRevoked => RevokedAt != null;

    public bool IsActive => !IsExpired && !IsRevoked;
}