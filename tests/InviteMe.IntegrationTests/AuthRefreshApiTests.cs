using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.IntegrationTests;

public sealed class AuthRefreshApiTests
{
    private const string Password = "Test-only-Pass123!";

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task LoginIssuesRefreshTokenAndRefreshRotatesIt() => WithApi(async (db, factory) =>
    {
        using var client = factory.CreateHttpsClient();
        var first = await Register(client, "rotate");
        Assert.False(string.IsNullOrWhiteSpace(first.RefreshToken));
        Assert.Equal(30 * 24 * 60 * 60, first.RefreshExpiresIn);
        Assert.Equal(0, await Count(db, $"SELECT count(*)::int AS \"Value\" FROM inviteme.refresh_tokens WHERE token_hash = '{first.RefreshToken}'"));
        Assert.Equal(1, await Count(db, "SELECT count(*)::int AS \"Value\" FROM inviteme.refresh_tokens"));

        var refreshed = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshSession(first.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var second = (await refreshed.Content.ReadFromJsonAsync<TokenDto>())!;
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.NotEqual(first.AccessToken, second.AccessToken);

        using var authenticated = factory.CreateHttpsClient();
        authenticated.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", second.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await authenticated.GetAsync("/api/auth/me")).StatusCode);

        await AssertInvalidRefresh(client, first.RefreshToken);
        Assert.Equal(2, await Count(db, "SELECT count(*)::int AS \"Value\" FROM inviteme.refresh_tokens"));
        Assert.Equal(1, await Count(db, "SELECT count(*)::int AS \"Value\" FROM inviteme.refresh_tokens WHERE replaced_by_id IS NOT NULL"));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ReusingARotatedTokenRevokesTheWholeFamily() => WithApi(async (db, factory) =>
    {
        using var client = factory.CreateHttpsClient();
        var first = await Register(client, "reuse");
        var second = (await (await client.PostAsJsonAsync("/api/auth/refresh", new RefreshSession(first.RefreshToken))).Content.ReadFromJsonAsync<TokenDto>())!;

        await AssertInvalidRefresh(client, first.RefreshToken);
        await AssertInvalidRefresh(client, second.RefreshToken);
        Assert.Equal(0, await Count(db, "SELECT count(*)::int AS \"Value\" FROM inviteme.refresh_tokens WHERE revoked_at IS NULL"));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task UnknownEmptyAndExpiredRefreshTokensAreRejected() => WithApi(async (db, factory) =>
    {
        using var client = factory.CreateHttpsClient();
        var token = await Register(client, "expired");

        await AssertInvalidRefresh(client, new string('a', 64));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/refresh", new RefreshSession(""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/refresh", new RefreshSession(new string('a', 257)))).StatusCode);

        await db.Database.ExecuteSqlRawAsync("UPDATE inviteme.refresh_tokens SET expires_at = NOW() - interval '1 minute'");
        await AssertInvalidRefresh(client, token.RefreshToken);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task LogoutRevokesEveryRefreshTokenOfTheUser() => WithApi(async (db, factory) =>
    {
        using var client = factory.CreateHttpsClient();
        var first = await Register(client, "logout");
        var second = (await (await client.PostAsJsonAsync("/api/auth/login", new LoginAccount("logout@example.test", Password))).Content.ReadFromJsonAsync<TokenDto>())!;

        using var authenticated = factory.CreateHttpsClient();
        authenticated.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", second.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, (await authenticated.PostAsync("/api/auth/logout", null)).StatusCode);

        await AssertInvalidRefresh(client, first.RefreshToken);
        await AssertInvalidRefresh(client, second.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await authenticated.GetAsync("/api/auth/me")).StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task OnlyOneConcurrentRefreshWithTheSameTokenSucceeds() => WithApi(async (db, factory) =>
    {
        using var client = factory.CreateHttpsClient();
        var token = await Register(client, "race");

        var results = await Task.WhenAll(
            client.PostAsJsonAsync("/api/auth/refresh", new RefreshSession(token.RefreshToken)),
            client.PostAsJsonAsync("/api/auth/refresh", new RefreshSession(token.RefreshToken)));

        Assert.Single(results, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.Unauthorized);

        // When concurrent requests race with the same token, the competing request detects reuse
        // and revokes the entire token family, ensuring stolen/replayed tokens cannot be leveraged.
        var okResult = results.Single(x => x.StatusCode == HttpStatusCode.OK);
        var okToken = (await okResult.Content.ReadFromJsonAsync<TokenDto>())!;
        await AssertInvalidRefresh(client, okToken.RefreshToken);
        Assert.Equal(0, await Count(db, "SELECT count(*)::int AS \"Value\" FROM inviteme.refresh_tokens WHERE revoked_at IS NULL"));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task RefreshFailsForAnAccountThatIsNoLongerActive() => WithApi(async (db, factory) =>
    {
        using var client = factory.CreateHttpsClient();
        var token = await Register(client, "inactive");

        await db.Database.ExecuteSqlRawAsync("UPDATE inviteme.users SET status = 'SUSPENDED' WHERE email = 'inactive@example.test'");

        await AssertInvalidRefresh(client, token.RefreshToken);
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

    private static async Task<TokenDto> Register(HttpClient client, string name)
    {
        var email = $"{name}@example.test";
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(email, Password, name))).StatusCode);
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginAccount(email, Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (await login.Content.ReadFromJsonAsync<TokenDto>())!;
    }

    private static async Task AssertInvalidRefresh(HttpClient client, string refreshToken)
    {
        var response = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshSession(refreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INVALID_REFRESH_TOKEN", problem.GetProperty("code").GetString());
    }

    private static Task<int> Count(InviteMeDbContext db, string sql) => db.Database.SqlQueryRaw<int>(sql).SingleAsync();
}
