using WaxyCandles.Infrastructure.Identity;

namespace WaxyCandles.Infrastructure.Authentication;

public interface ITokenService
{
    string GenerateAccessToken(ApplicationUser user);

    string GenerateRefreshToken();
}