using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Infrastructure.Identity;
using InviteMe.Infrastructure.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace InviteMe.IntegrationTests;

public sealed class AuthRefreshApiTests
{
    private const string Password = "Test-only-Pass123!";

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task LoginIssuesRefreshTokenAndRefreshRotatesIt() => WithApi(async (db, factory) =>
    {
        using var client = factory.CreateHttpsClient();
        var (first, firstCookie) = await Register(client, "rotate");
        Assert.False(string.IsNullOrWhiteSpace(firstCookie));
        Assert.Equal(0, await Count(db, $"SELECT count(*)::int AS \"Value\" FROM inviteme.refresh_tokens WHERE token_hash = '{firstCookie}'"));
        Assert.Equal(1, await Count(db, "SELECT count(*)::int AS \"Value\" FROM inviteme.refresh_tokens"));

        var refreshed = await SendRefresh(client, firstCookie);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var rawJson = await refreshed.Content.ReadAsStringAsync();
        Assert.DoesNotContain("refreshToken", rawJson, StringComparison.OrdinalIgnoreCase);

        var second = (await refreshed.Content.ReadFromJsonAsync<TokenDto>())!;
        var secondCookie = ExtractRefreshCookie(refreshed);
        Assert.NotEqual(firstCookie, secondCookie);
        Assert.NotEqual(first.AccessToken, second.AccessToken);

        using var authenticated = factory.CreateHttpsClient();
        authenticated.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", second.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await authenticated.GetAsync("/api/auth/me")).StatusCode);

        await AssertInvalidRefresh(client, firstCookie);
        Assert.Equal(2, await Count(db, "SELECT count(*)::int AS \"Value\" FROM inviteme.refresh_tokens"));
        Assert.Equal(1, await Count(db, "SELECT count(*)::int AS \"Value\" FROM inviteme.refresh_tokens WHERE replaced_by_id IS NOT NULL"));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ReusingARotatedTokenRevokesTheWholeFamily() => WithApi(async (db, factory) =>
    {
        using var client = factory.CreateHttpsClient();
        var (_, firstCookie) = await Register(client, "reuse");
        var refreshRes = await SendRefresh(client, firstCookie);
        var secondCookie = ExtractRefreshCookie(refreshRes);

        await AssertInvalidRefresh(client, firstCookie);
        await AssertInvalidRefresh(client, secondCookie);
        Assert.Equal(0, await Count(db, "SELECT count(*)::int AS \"Value\" FROM inviteme.refresh_tokens WHERE revoked_at IS NULL"));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task UnknownEmptyAndExpiredRefreshTokensAreRejected() => WithApi(async (db, factory) =>
    {
        using var client = factory.CreateHttpsClient();
        var (_, cookie) = await Register(client, "expired");

        // Missing cookie -> 401 INVALID_REFRESH_TOKEN + clears cookie
        var missingRes = await client.PostAsync("/api/auth/refresh", null);
        await AssertInvalidRefreshResponse(missingRes);

        await AssertInvalidRefresh(client, new string('a', 64));
        await AssertInvalidRefresh(client, "");
        await AssertInvalidRefresh(client, new string('a', 257));

        await db.Database.ExecuteSqlRawAsync("UPDATE inviteme.refresh_tokens SET expires_at = NOW() - interval '1 minute'");
        await AssertInvalidRefresh(client, cookie);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task LogoutRevokesEveryRefreshTokenOfTheUserAndSupportsExpiredAccessToken() => WithApi(async (db, factory) =>
    {
        using var client = factory.CreateHttpsClient();
        var (_, firstCookie) = await Register(client, "logout");
        var loginRes = await client.PostAsJsonAsync("/api/auth/login", new LoginAccount("logout@example.test", Password));
        var second = (await loginRes.Content.ReadFromJsonAsync<TokenDto>())!;
        var secondCookie = ExtractRefreshCookie(loginRes);

        var user = await db.Set<ApplicationUser>().AsNoTracking().SingleAsync(u => u.Email == "logout@example.test");
        var expiredAccessToken = CreateExpiredToken(user.Id, user.SecurityStamp!);

        // Ordinary endpoint rejects expired token with 401 ACCESS_TOKEN_EXPIRED
        using var expiredClient = factory.CreateHttpsClient();
        expiredClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", expiredAccessToken);
        var meExpired = await expiredClient.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, meExpired.StatusCode);
        var meExpiredProblem = await meExpired.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ACCESS_TOKEN_EXPIRED", meExpiredProblem.GetProperty("code").GetString());

        // Logout with expired access token still succeeds, revokes all sessions, and deletes cookie with Path=/api/auth
        var logoutRes = await expiredClient.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logoutRes.StatusCode);
        AssertCookieDeleted(logoutRes);

        await AssertInvalidRefresh(client, firstCookie);
        await AssertInvalidRefresh(client, secondCookie);

        // Unexpired access token from before logout now fails with 401 SESSION_REVOKED
        using var authenticated = factory.CreateHttpsClient();
        authenticated.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", second.AccessToken);
        var meRevoked = await authenticated.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, meRevoked.StatusCode);
        var meRevokedProblem = await meRevoked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("SESSION_REVOKED", meRevokedProblem.GetProperty("code").GetString());
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task DatabaseFailureDuringRefreshDoesNotTreatAsInvalidTokenOrDeleteCookie() => WithApi(async (_, baseFactory) =>
    {
        await using var failingFactory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IRefreshTokenStore>();
            services.AddScoped<IRefreshTokenStore, ThrowingRefreshTokenStore>();
        }));

        using var client = failingFactory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        var response = await SendRefresh(client, "valid-looking-refresh-token");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INTERNAL_ERROR", problem.GetProperty("code").GetString());
        Assert.False(response.Headers.Contains("Set-Cookie"));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task OnlyOneConcurrentRefreshWithTheSameTokenSucceeds() => WithApi(async (db, factory) =>
    {
        using var client = factory.CreateHttpsClient();
        var (_, cookie) = await Register(client, "race");

        var results = await Task.WhenAll(
            SendRefresh(client, cookie),
            SendRefresh(client, cookie));

        Assert.Single(results, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.Unauthorized);

        // When concurrent requests race with the same token, the competing request detects reuse
        // and revokes the entire token family, ensuring stolen/replayed tokens cannot be leveraged.
        var okResult = results.Single(x => x.StatusCode == HttpStatusCode.OK);
        var okCookie = ExtractRefreshCookie(okResult);
        await AssertInvalidRefresh(client, okCookie);
        Assert.Equal(0, await Count(db, "SELECT count(*)::int AS \"Value\" FROM inviteme.refresh_tokens WHERE revoked_at IS NULL"));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task RefreshFailsForAnAccountThatIsNoLongerActive() => WithApi(async (db, factory) =>
    {
        using var client = factory.CreateHttpsClient();
        var (_, cookie) = await Register(client, "inactive");

        await db.Database.ExecuteSqlRawAsync("UPDATE inviteme.users SET status = 'SUSPENDED' WHERE email = 'inactive@example.test'");

        await AssertInvalidRefresh(client, cookie);
        Assert.Equal(0, await Count(db, "SELECT count(*)::int AS \"Value\" FROM inviteme.refresh_tokens WHERE revoked_at IS NULL"));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task RepeatedWrongPasswordsLockTheAccountEvenForTheCorrectPassword() => WithApi(async (_, factory) =>
    {
        using var client = factory.CreateHttpsClient();
        await Register(client, "lockout");

        for (var i = 0; i < 5; i++)
        {
            var bad = await client.PostAsJsonAsync("/api/auth/login", new LoginAccount("lockout@example.test", "Wrong-Pass123!"));
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        }

        // 6th attempt with the CORRECT password is still rejected because the account is temporarily locked out (15 minutes)
        var locked = await client.PostAsJsonAsync("/api/auth/login", new LoginAccount("lockout@example.test", Password));
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ExceedingAuthRateLimitReturns429TooManyRequests() => WithApi(async (_, factory) =>
    {
        using var client = factory.CreateHttpsClient();

        HttpStatusCode lastStatus = HttpStatusCode.OK;
        for (var i = 0; i < 21; i++)
        {
            var res = await client.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPassword("ratelimit@example.test"));
            lastStatus = res.StatusCode;
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, lastStatus);
    });

    private static Task WithApi(Func<InviteMeDbContext, DatabaseApiFactory, Task> action) => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        await using var factory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
        await action(db, factory);
    });

    private static async Task<(TokenDto Token, string RefreshCookie)> Register(HttpClient client, string name)
    {
        var email = $"{name}@example.test";
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(email, Password, name))).StatusCode);
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginAccount(email, Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var rawJson = await login.Content.ReadAsStringAsync();
        Assert.DoesNotContain("refreshToken", rawJson, StringComparison.OrdinalIgnoreCase);
        var token = (await login.Content.ReadFromJsonAsync<TokenDto>())!;
        var cookie = ExtractRefreshCookie(login);
        return (token, cookie);
    }

    internal static string ExtractRefreshCookie(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var values));
        var header = values.Single(x => x.StartsWith("refresh_token=", StringComparison.Ordinal));
        Assert.Contains("httponly", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/auth", header, StringComparison.OrdinalIgnoreCase);
        var match = Regex.Match(header, @"^refresh_token=([^;]+)");
        Assert.True(match.Success);
        return Uri.UnescapeDataString(match.Groups[1].Value);
    }

    internal static void AssertCookieDeleted(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var values));
        var header = values.Single(x => x.StartsWith("refresh_token=", StringComparison.Ordinal));
        Assert.Contains("refresh_token=;", header, StringComparison.Ordinal);
        Assert.Contains("path=/api/auth", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", header, StringComparison.OrdinalIgnoreCase);
    }

    internal static Task<HttpResponseMessage> SendRefresh(HttpClient client, string refreshCookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        request.Headers.Add("Cookie", $"refresh_token={Uri.EscapeDataString(refreshCookie)}");
        return client.SendAsync(request);
    }

    private static async Task AssertInvalidRefresh(HttpClient client, string refreshToken)
    {
        var response = await SendRefresh(client, refreshToken);
        await AssertInvalidRefreshResponse(response);
    }

    private static async Task AssertInvalidRefreshResponse(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INVALID_REFRESH_TOKEN", problem.GetProperty("code").GetString());
        AssertCookieDeleted(response);
    }

    private static string CreateExpiredToken(Guid userId, string sessionStamp)
    {
        var claims = new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("role", "USER"),
            new Claim("session_stamp", sessionStamp),
            new Claim("jti", Guid.NewGuid().ToString())
        };
        var token = new JwtSecurityToken(
            issuer: "InviteMe.Tests",
            audience: "InviteMe.Tests.Web",
            claims: claims,
            notBefore: DateTime.UtcNow.AddHours(-2),
            expires: DateTime.UtcNow.AddMinutes(-10),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(ApiFactory.SigningKey), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static Task<int> Count(InviteMeDbContext db, string sql) => db.Database.SqlQueryRaw<int>(sql).SingleAsync();

    private sealed class ThrowingRefreshTokenStore : IRefreshTokenStore
    {
        public Task<IssuedRefreshToken> IssueAsync(Guid userId, TimeSpan lifetime, CancellationToken ct)
            => throw new InvalidOperationException("Simulated database outage.");
        public Task<RefreshRotation?> RotateAsync(string refreshToken, TimeSpan lifetime, CancellationToken ct)
            => throw new InvalidOperationException("Simulated database outage.");
        public Task<Guid?> FindUserIdByActiveTokenAsync(string refreshToken, CancellationToken ct)
            => throw new InvalidOperationException("Simulated database outage.");
        public Task RevokeAllAsync(Guid userId, CancellationToken ct)
            => throw new InvalidOperationException("Simulated database outage.");
    }
}
