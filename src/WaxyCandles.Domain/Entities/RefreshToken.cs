namespace WaxyCandles.Domain.Entities;

public class RefreshToken
{
    public Guid Id { get; set; }

    // SHA-256 of the token handed to the client (TokenService.HashRefreshToken).
    // The plaintext is never stored, so a database leak gives no usable session.
    public byte[] TokenHash { get; set; } = [];

    public string UserId { get; set; } = string.Empty;

    // Every token descended from one login or registration shares this id: it
    // is new at sign-in and copied on each rotation. Replaying a token that was
    // already rotated revokes the whole family in one UPDATE, so a stolen and
    // used token also ends the session it was rotated into.
    public Guid FamilyId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    // Why RevokedAt is set; null while the token is not revoked. Only a token
    // revoked by rotation triggers reuse detection when presented again.
    public RefreshTokenRevocationReason? RevokedReason { get; set; }

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;

    public bool IsRevoked => RevokedAt != null;

    public bool IsActive => !IsExpired && !IsRevoked;
}

// Stored as int; never renumber.
public enum RefreshTokenRevocationReason
{
    // Exchanged for a new token in the same family.
    Rotated = 1,

    // The user logged out of this session.
    Logout = 2,

    // An already-rotated token of this family was presented again.
    ReuseDetected = 3
}
