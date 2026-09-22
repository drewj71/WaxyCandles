using Microsoft.AspNetCore.Identity;

namespace WaxyCandles.Infrastructure.Identity;

public class ApplicationUser : IdentityUser
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLogin { get; set; }
    public bool IsActive { get; set; } = true;
}