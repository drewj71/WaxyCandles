using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using WaxyCandles.Infrastructure.Identity;
using WaxyCandles.Tests.Infrastructure;

namespace WaxyCandles.Tests.Api;

/// <summary>
/// The cookie-based auth flow as configured in Program.cs and AddApiAuthentication:
/// the JWT is read from the waxy_access_token cookie, not the Authorization header.
/// </summary>
public class AuthCookieFlowTests(IntegrationFixture fixture) : ApiTestBase(fixture)
{
    public static TheoryData<string> ProtectedEndpoints =>
    [
        "/api/auth/me",
        "/api/users/me",
        "/api/alerts",
        "/api/watchlist",
        "/api/watchlist/quotes",
        "/api/stocks/AAPL"
    ];

    [Theory]
    [MemberData(nameof(ProtectedEndpoints))]
    public async Task Protected_endpoints_reject_anonymous_requests(string url)
    {
        var response = await NewClient().Http.GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Register_sets_both_tokens_as_httponly_secure_samesite_cookies()
    {
        var response = await RegisterAsync(NewClient().Http, "alice@example.com");

        response.EnsureSuccessStatusCode();

        var setCookies = response.Headers.GetValues("Set-Cookie").ToList();

        foreach (var name in new[] { AccessTokenCookie, RefreshTokenCookie })
        {
            var header = Assert.Single(setCookies, c => c.StartsWith($"{name}="));

            Assert.Contains("httponly", header, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("secure", header, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=lax", header, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Registered_user_is_authenticated_by_the_cookie_alone()
    {
        var alice = await RegisteredClientAsync("alice@example.com");

        var me = await alice.Http.GetFromJsonAsync<MeResponse>("/api/auth/me", Json);
        var usersMe = await alice.Http.GetFromJsonAsync<MeResponse>("/api/users/me", Json);

        Assert.Equal("alice@example.com", me!.Email);
        Assert.False(string.IsNullOrEmpty(me.Id));
        Assert.Equal(me, usersMe);
    }

    [Fact]
    public async Task Login_from_a_new_client_issues_working_cookies()
    {
        await RegisteredClientAsync("alice@example.com");

        var browser = NewClient();
        (await LoginAsync(browser.Http, "alice@example.com")).EnsureSuccessStatusCode();

        Assert.NotNull(browser.Cookie(AccessTokenCookie));
        Assert.NotNull(browser.Cookie(RefreshTokenCookie));
        Assert.Equal(
            HttpStatusCode.OK,
            (await browser.Http.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Wrong_password_does_not_issue_cookies()
    {
        await RegisteredClientAsync("alice@example.com");

        var browser = NewClient();
        var response = await LoginAsync(browser.Http, "alice@example.com", "WrongPassword1");

        Assert.False(response.IsSuccessStatusCode);
        Assert.Null(browser.Cookie(AccessTokenCookie));
        Assert.Null(browser.Cookie(RefreshTokenCookie));
    }

    [Fact]
    public async Task Duplicate_registration_is_rejected()
    {
        await RegisteredClientAsync("alice@example.com");

        var response = await RegisterAsync(NewClient().Http, "alice@example.com");

        Assert.False(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Bearer_header_is_also_accepted()
    {
        var alice = await RegisteredClientAsync("alice@example.com");

        var raw = NewRawClient();
        raw.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", alice.Cookie(AccessTokenCookie));

        Assert.Equal(HttpStatusCode.OK, (await raw.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Correctly_signed_token_is_accepted()
    {
        // Control for the forged-token tests below: proves the token helper itself
        // produces something the API accepts, so their 401s come from the key/expiry.
        var userId = await CreateUserAsync("alice@example.com");
        var token = CreateToken(userId, IntegrationFixture.TestJwtSecret);

        var response = await NewRawClient().SendAsync(
            WithCookie(HttpMethod.Get, "/api/auth/me", AccessTokenCookie, token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Token_signed_with_a_different_key_is_rejected()
    {
        var userId = await CreateUserAsync("alice@example.com");
        var forged = CreateToken(userId, "an-attackers-key-that-is-long-enough-for-hs256!!");

        var response = await NewRawClient().SendAsync(
            WithCookie(HttpMethod.Get, "/api/auth/me", AccessTokenCookie, forged));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        var userId = await CreateUserAsync("alice@example.com");

        // Beyond the default 5 minute clock skew.
        var expired = CreateToken(
            userId,
            IntegrationFixture.TestJwtSecret,
            expires: DateTime.UtcNow.AddMinutes(-10));

        var response = await NewRawClient().SendAsync(
            WithCookie(HttpMethod.Get, "/api/auth/me", AccessTokenCookie, expired));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_rotates_both_cookies_and_the_new_ones_work()
    {
        var alice = await RegisteredClientAsync("alice@example.com");
        var oldAccess = alice.Cookie(AccessTokenCookie);
        var oldRefresh = alice.Cookie(RefreshTokenCookie);

        (await alice.Http.PostAsync("/api/auth/refresh", null)).EnsureSuccessStatusCode();

        Assert.NotEqual(oldAccess, alice.Cookie(AccessTokenCookie));
        Assert.NotEqual(oldRefresh, alice.Cookie(RefreshTokenCookie));
        Assert.Equal(HttpStatusCode.OK, (await alice.Http.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Replaying_a_rotated_refresh_cookie_is_rejected()
    {
        var alice = await RegisteredClientAsync("alice@example.com");
        var stolenRefresh = alice.Cookie(RefreshTokenCookie)!;

        (await alice.Http.PostAsync("/api/auth/refresh", null)).EnsureSuccessStatusCode();

        var replay = await NewRawClient().SendAsync(
            WithCookie(HttpMethod.Post, "/api/auth/refresh", RefreshTokenCookie, stolenRefresh));

        Assert.False(replay.IsSuccessStatusCode);
        Assert.DoesNotContain(
            replay.Headers.TryGetValues("Set-Cookie", out var set) ? set : [],
            c => c.StartsWith($"{AccessTokenCookie}="));
    }

    [Fact]
    public async Task Refresh_without_a_cookie_is_unauthorized()
    {
        var response = await NewClient().Http.PostAsync("/api/auth/refresh", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_clears_cookies_and_revokes_the_refresh_token()
    {
        var alice = await RegisteredClientAsync("alice@example.com");
        var refreshToken = alice.Cookie(RefreshTokenCookie)!;

        (await alice.Http.PostAsync("/api/auth/logout", null)).EnsureSuccessStatusCode();

        Assert.Null(alice.Cookie(AccessTokenCookie));
        Assert.Null(alice.Cookie(RefreshTokenCookie));
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await alice.Http.GetAsync("/api/auth/me")).StatusCode);

        var replay = await NewRawClient().SendAsync(
            WithCookie(HttpMethod.Post, "/api/auth/refresh", RefreshTokenCookie, refreshToken));

        Assert.False(replay.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Access_token_stays_valid_after_logout_until_it_expires()
    {
        // Current behavior, inherent to stateless JWTs: logout revokes the refresh
        // token and deletes the cookies, but a copied access token keeps working
        // for the rest of its 15 minute lifetime.
        var alice = await RegisteredClientAsync("alice@example.com");
        var copiedAccess = alice.Cookie(AccessTokenCookie)!;

        (await alice.Http.PostAsync("/api/auth/logout", null)).EnsureSuccessStatusCode();

        var response = await NewRawClient().SendAsync(
            WithCookie(HttpMethod.Get, "/api/auth/me", AccessTokenCookie, copiedAccess));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Auth_failures_are_reported_as_client_errors()
    {
        var alice = await RegisteredClientAsync("alice@example.com");
        var rotatedRefresh = alice.Cookie(RefreshTokenCookie)!;
        (await alice.Http.PostAsync("/api/auth/refresh", null)).EnsureSuccessStatusCode();

        var wrongPassword = await LoginAsync(NewClient().Http, "alice@example.com", "WrongPassword1");
        var unknownUser = await LoginAsync(NewClient().Http, "nobody@example.com");
        var duplicate = await RegisterAsync(NewClient().Http, "alice@example.com");
        var weakPassword = await RegisterAsync(NewClient().Http, "bob@example.com", "short");
        var revokedRefresh = await NewRawClient().SendAsync(
            WithCookie(HttpMethod.Post, "/api/auth/refresh", RefreshTokenCookie, rotatedRefresh));

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, weakPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedRefresh.StatusCode);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_get_the_same_message()
    {
        await RegisteredClientAsync("alice@example.com");

        var wrongPassword = await LoginAsync(NewClient().Http, "alice@example.com", "WrongPassword1");
        var unknownUser = await LoginAsync(NewClient().Http, "nobody@example.com");

        Assert.Equal("Invalid credentials.", await ErrorMessageAsync(wrongPassword));
        Assert.Equal("Invalid credentials.", await ErrorMessageAsync(unknownUser));
    }

    [Fact]
    public async Task Weak_password_reports_the_identity_policy_errors()
    {
        var response = await RegisterAsync(NewClient().Http, "bob@example.com", "short");

        var message = await ErrorMessageAsync(response);
        Assert.Contains("Passwords must be at least 8 characters", message);
    }

    [Fact]
    public async Task Registration_with_mismatched_confirm_password_is_rejected()
    {
        var response = await RegisterAsync(
            NewClient().Http,
            "alice@example.com",
            password: Password,
            confirmPassword: "SomethingElse1");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Passwords do not match.", await ErrorMessageAsync(response));
        Assert.Null(await FindUserAsync("alice@example.com"));
    }

    private static async Task<string?> ErrorMessageAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ErrorResponse>(Json))?.Message;

    private sealed record ErrorResponse(string Message);

    private async Task<string> CreateUserAsync(string email)
    {
        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = new ApplicationUser { UserName = email, Email = email };
        var result = await users.CreateAsync(user, Password);
        Assert.True(result.Succeeded);

        return user.Id;
    }

    private async Task<ApplicationUser?> FindUserAsync(string email)
    {
        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return await users.FindByEmailAsync(email);
    }

    private static string CreateToken(string userId, string signingKey, DateTime? expires = null)
    {
        var expiry = expires ?? DateTime.UtcNow.AddMinutes(15);

        var token = new JwtSecurityToken(
            issuer: IntegrationFixture.TestJwtIssuer,
            audience: IntegrationFixture.TestJwtAudience,
            claims: [new Claim(ClaimTypes.NameIdentifier, userId)],
            notBefore: expiry.AddMinutes(-30),
            expires: expiry,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed record MeResponse(string Id, string Email);
}
