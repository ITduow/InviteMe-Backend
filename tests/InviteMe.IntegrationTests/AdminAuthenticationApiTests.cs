using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Infrastructure.Identity;
using InviteMe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace InviteMe.IntegrationTests;

public sealed class AdminAuthenticationApiTests
{
    private const string AdminEmail = "bootstrap-admin@example.test";
    private const string AdminPassword = "Bootstrap-Admin-Test-2026!";
    private const string UserPassword = "Test-only-Pass123!";

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task BootstrapIsIdempotentAndAdminLoginUsesRefreshAndLogoutSafely() =>
        SchemaV2MigrationTests.InDatabase(async db =>
        {
            var config = BootstrapConfiguration(AdminEmail);
            await using var firstFactory = new DatabaseApiFactory(db.Database.GetConnectionString()!, additionalConfiguration: config);
            using var client = firstFactory.CreateHttpsClient();

            var login = await client.PostAsJsonAsync("/api/admin/auth/login", new LoginAccount(AdminEmail, AdminPassword));
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var token = (await login.Content.ReadFromJsonAsync<TokenDto>())!;
            var firstCookie = ExtractRefreshCookie(login);
            Assert.False(string.IsNullOrWhiteSpace(token.AccessToken));
            Assert.DoesNotContain("refreshToken", await login.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

            using var adminClient = firstFactory.CreateHttpsClient();
            adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            Assert.Equal(HttpStatusCode.OK, (await adminClient.GetAsync("/api/admin/dashboard")).StatusCode);

            // A second application instance repeats bootstrap against the same database.
            await using var secondFactory = new DatabaseApiFactory(db.Database.GetConnectionString()!, additionalConfiguration: config);
            using var secondClient = secondFactory.CreateHttpsClient();
            Assert.Equal(HttpStatusCode.OK, (await secondClient.GetAsync("/health/ready")).StatusCode);
            Assert.Equal(1, await db.Users.CountAsync(x => x.Email == AdminEmail));
            Assert.Equal(1, await db.UserRoles.CountAsync(x => x.UserId == db.Users.Where(u => u.Email == AdminEmail).Select(u => u.Id).Single()));
            Assert.Contains(await db.AuditLogs.AsNoTracking().ToListAsync(), x => x.Action == "ADMIN_LOGIN");

            var refreshRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
            refreshRequest.Headers.Add("Cookie", firstCookie);
            var refreshed = await client.SendAsync(refreshRequest);
            Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
            var nextCookie = ExtractRefreshCookie(refreshed);
            Assert.NotEqual(firstCookie, nextCookie);
            var nextToken = (await refreshed.Content.ReadFromJsonAsync<TokenDto>())!;

            var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
            logoutRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", nextToken.AccessToken);
            logoutRequest.Headers.Add("Cookie", nextCookie);
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(logoutRequest)).StatusCode);

            var user = await db.Users.SingleAsync(x => x.Email == AdminEmail);
            var expiredToken = CreateExpiredAdminToken(user.Id, user.SecurityStamp!);
            using var expiredClient = firstFactory.CreateHttpsClient();
            expiredClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", expiredToken);
            var expiredResponse = await expiredClient.GetAsync("/api/admin/dashboard");
            Assert.Equal(HttpStatusCode.Unauthorized, expiredResponse.StatusCode);
            var expiredProblem = await expiredResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("ACCESS_TOKEN_EXPIRED", expiredProblem.GetProperty("code").GetString());

            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inviteme.users SET status = 'SUSPENDED' WHERE id = {user.Id}");
            var disabledLogin = await client.PostAsJsonAsync("/api/admin/auth/login", new LoginAccount(AdminEmail, AdminPassword));
            Assert.Equal(HttpStatusCode.Unauthorized, disabledLogin.StatusCode);
            Assert.DoesNotContain(disabledLogin.Headers, header => header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase));
        });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task RegularUsersCannotLoginOrReachAdminEndpointsAndPayloadCannotPromoteThem() =>
        SchemaV2MigrationTests.InDatabase(async db =>
        {
            await using var factory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
            using var client = factory.CreateHttpsClient();

            var registration = await client.PostAsJsonAsync("/api/auth/register", new
            {
                email = "ordinary-admin-probe@example.test",
                password = UserPassword,
                displayName = "Ordinary User",
                role = "ADMIN"
            });
            Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
            var registered = (await registration.Content.ReadFromJsonAsync<AccountDto>())!;
            Assert.Equal("USER", registered.Role);

            var adminLogin = await client.PostAsJsonAsync("/api/admin/auth/login", new LoginAccount(registered.Email, UserPassword));
            Assert.Equal(HttpStatusCode.Unauthorized, adminLogin.StatusCode);
            Assert.DoesNotContain(adminLogin.Headers, header => header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase));

            var userLogin = await client.PostAsJsonAsync("/api/auth/login", new LoginAccount(registered.Email, UserPassword));
            Assert.Equal(HttpStatusCode.OK, userLogin.StatusCode);
            var userToken = (await userLogin.Content.ReadFromJsonAsync<TokenDto>())!;
            using var userClient = factory.CreateHttpsClient();
            userClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", userToken.AccessToken);

            var adminDashboard = await userClient.GetAsync("/api/admin/dashboard");
            Assert.Equal(HttpStatusCode.Forbidden, adminDashboard.StatusCode);
            var roleMutation = await userClient.PutAsJsonAsync("/api/admin/users/00000000-0000-0000-0000-000000000001/role", new { role = "ADMIN" });
            Assert.Equal(HttpStatusCode.Forbidden, roleMutation.StatusCode);

            var profile = await userClient.PutAsJsonAsync("/api/auth/me", new
            {
                displayName = "Ordinary User Updated",
                phone = (string?)null,
                avatarUrl = (string?)null,
                role = "ADMIN"
            });
            Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
            var updated = (await profile.Content.ReadFromJsonAsync<AccountDto>())!;
            Assert.Equal("USER", updated.Role);

            using var anonymous = factory.CreateHttpsClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/dashboard")).StatusCode);
        });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task AdminLoginLockoutRejectsRepeatedBadPasswords() =>
        SchemaV2MigrationTests.InDatabase(async db =>
        {
            await using var factory = new DatabaseApiFactory(db.Database.GetConnectionString()!, additionalConfiguration: BootstrapConfiguration(AdminEmail));
            using var client = factory.CreateHttpsClient();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);

            var user = await db.Users.SingleAsync(x => x.Email == AdminEmail);
            user.AccessFailedCount = 4;
            await db.SaveChangesAsync();

            var failedPassword = await client.PostAsJsonAsync("/api/admin/auth/login", new LoginAccount(AdminEmail, "Wrong-password-123!"));
            Assert.Equal(HttpStatusCode.Unauthorized, failedPassword.StatusCode);
            await db.Entry(user).ReloadAsync();
            Assert.True(user.LockoutEnd.HasValue && user.LockoutEnd > DateTimeOffset.UtcNow);

            var correctPasswordWhileLocked = await client.PostAsJsonAsync("/api/admin/auth/login", new LoginAccount(AdminEmail, AdminPassword));
            Assert.Equal(HttpStatusCode.Unauthorized, correctPasswordWhileLocked.StatusCode);

            // The rate limiter is IP-partitioned and caps Admin login independently of account lockout.
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var failed = await client.PostAsJsonAsync("/api/admin/auth/login", new LoginAccount(AdminEmail, "Wrong-password-123!"));
                Assert.Equal(attempt < 3 ? HttpStatusCode.Unauthorized : HttpStatusCode.TooManyRequests, failed.StatusCode);
            }
        });

    private static Dictionary<string, string?> BootstrapConfiguration(string email) => new()
    {
        ["AdminBootstrap:Enabled"] = "true",
        ["AdminBootstrap:Email"] = email,
        ["AdminBootstrap:Password"] = AdminPassword,
        ["AdminBootstrap:DisplayName"] = "Bootstrap Administrator"
    };

    private static string ExtractRefreshCookie(HttpResponseMessage response)
    {
        var raw = Assert.Single(response.Headers.GetValues("Set-Cookie"), x => x.StartsWith("refresh_token=", StringComparison.Ordinal));
        return raw.Split(';', 2)[0];
    }

    private static string CreateExpiredAdminToken(Guid userId, string sessionStamp)
    {
        var claims = new[]
        {
            new Claim("sub", userId.ToString()),
            new Claim("role", "ADMIN"),
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
}
