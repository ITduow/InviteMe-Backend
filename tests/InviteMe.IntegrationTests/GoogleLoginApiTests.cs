using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Infrastructure.Identity;
using InviteMe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InviteMe.IntegrationTests;

public sealed class GoogleLoginApiTests
{
    private const string Password = "Test-only-Pass123!";

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task GoogleLoginCreatesNewUserWithUserRoleAndIssuesTokens() => WithApi(async (validator, client, db) =>
    {
        validator.RegisterToken("token-new-user", new GoogleUserPayload(
            Subject: "google-sub-001",
            Email: "newgoogle@example.test",
            EmailVerified: true,
            Name: "New Google User",
            Picture: "https://avatar.test/pic.png"));

        var response = await client.PostAsJsonAsync("/api/auth/google", new GoogleLogin("token-new-user"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var tokens = await response.Content.ReadFromJsonAsync<TokenDto>();
        Assert.NotNull(tokens);
        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(tokens.RefreshToken));

        // Use the token to fetch /api/auth/me
        using var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        meRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var meResponse = await client.SendAsync(meRequest);
        Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);

        var me = await meResponse.Content.ReadFromJsonAsync<AccountDto>();
        Assert.NotNull(me);
        Assert.Equal("newgoogle@example.test", me.Email);
        Assert.Equal("New Google User", me.DisplayName);
        Assert.Equal("USER", me.Role);

        // Verify database state: user has avatar and email is confirmed
        var user = await db.Set<ApplicationUser>().SingleAsync(u => u.Email == "newgoogle@example.test");
        Assert.True(user.EmailConfirmed);
        Assert.Equal("https://avatar.test/pic.png", user.AvatarUrl);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task GoogleLoginLinksExistingPasswordAccount() => WithApi(async (validator, client, db) =>
    {
        // First, register standard account with password and confirm email
        var email = "existing@example.test";
        var reg = await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(email, Password, "Existing User"));
        Assert.Equal(HttpStatusCode.Created, reg.StatusCode);

        var existingUser = await db.Set<ApplicationUser>().SingleAsync(u => u.Email == email);
        existingUser.EmailConfirmed = true;
        await db.SaveChangesAsync();

        // Next, log in with Google using same email
        validator.RegisterToken("token-existing-user", new GoogleUserPayload(
            Subject: "google-sub-002",
            Email: email,
            EmailVerified: true,
            Name: "Existing Google Name",
            Picture: "https://avatar.test/existing.png"));

        var googleResponse = await client.PostAsJsonAsync("/api/auth/google", new GoogleLogin("token-existing-user"));
        Assert.Equal(HttpStatusCode.OK, googleResponse.StatusCode);

        var tokens = await googleResponse.Content.ReadFromJsonAsync<TokenDto>();
        Assert.NotNull(tokens);

        // User can still log in with standard password
        var passwordLogin = await client.PostAsJsonAsync("/api/auth/login", new LoginAccount(email, Password));
        Assert.Equal(HttpStatusCode.OK, passwordLogin.StatusCode);

        // User can log in with Google a second time
        var secondGoogle = await client.PostAsJsonAsync("/api/auth/google", new GoogleLogin("token-existing-user"));
        Assert.Equal(HttpStatusCode.OK, secondGoogle.StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task GoogleLoginUnconfirmedEmptyAccountWipesPasswordToPreventPreHijacking() => WithApi(async (validator, client, db) =>
    {
        // Attacker creates unconfirmed account with victim's email
        var email = "victim@example.test";
        var reg = await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(email, Password, "Unconfirmed Holder"));
        Assert.Equal(HttpStatusCode.Created, reg.StatusCode);

        // Victim authenticates with Google
        validator.RegisterToken("token-victim", new GoogleUserPayload(
            Subject: "google-sub-victim",
            Email: email,
            EmailVerified: true,
            Name: "Victim Real Name",
            Picture: null));

        var googleResponse = await client.PostAsJsonAsync("/api/auth/google", new GoogleLogin("token-victim"));
        Assert.Equal(HttpStatusCode.OK, googleResponse.StatusCode);

        // Password hash was wiped, so attacker cannot log in with the previous password
        var attackerLogin = await client.PostAsJsonAsync("/api/auth/login", new LoginAccount(email, Password));
        Assert.Equal(HttpStatusCode.Unauthorized, attackerLogin.StatusCode);

        // Victim account is now email-confirmed
        var user = await db.Set<ApplicationUser>().SingleAsync(u => u.Email == email);
        Assert.True(user.EmailConfirmed);
        Assert.Null(user.PasswordHash);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task GoogleLoginRejectsInvalidOrUnverifiedToken() => WithApi(async (validator, client, _) =>
    {
        // Token not registered -> returns null payload -> 401
        var invalid = await client.PostAsJsonAsync("/api/auth/google", new GoogleLogin("unrecognized-token"));
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);

        // Token with unverified email -> 401
        validator.RegisterToken("token-unverified", new GoogleUserPayload(
            Subject: "google-sub-003",
            Email: "unverified@example.test",
            EmailVerified: false,
            Name: "Unverified User",
            Picture: null));

        var unverified = await client.PostAsJsonAsync("/api/auth/google", new GoogleLogin("token-unverified"));
        Assert.Equal(HttpStatusCode.Unauthorized, unverified.StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task GoogleLoginRejectsSuspendedUser() => WithApi(async (validator, client, db) =>
    {
        validator.RegisterToken("token-suspended", new GoogleUserPayload(
            Subject: "google-sub-004",
            Email: "suspended@example.test",
            EmailVerified: true,
            Name: "Suspended User",
            Picture: null));

        // Create initial account
        var res = await client.PostAsJsonAsync("/api/auth/google", new GoogleLogin("token-suspended"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        // Suspend user in db
        var user = await db.Set<ApplicationUser>().SingleAsync(u => u.Email == "suspended@example.test");
        user.Status = "SUSPENDED";
        await db.SaveChangesAsync();

        // Subsequent Google login is rejected
        var retry = await client.PostAsJsonAsync("/api/auth/google", new GoogleLogin("token-suspended"));
        Assert.Equal(HttpStatusCode.Unauthorized, retry.StatusCode);
    });

    private static Task WithApi(Func<TestGoogleTokenValidator, HttpClient, InviteMeDbContext, Task> action) => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        var validator = new TestGoogleTokenValidator();
        await using var baseFactory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IGoogleTokenValidator>();
            services.AddSingleton<IGoogleTokenValidator>(validator);
        }));
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        await action(validator, client, db);
    });

    private sealed class TestGoogleTokenValidator : IGoogleTokenValidator
    {
        private readonly Dictionary<string, GoogleUserPayload> _tokens = new();

        public void RegisterToken(string token, GoogleUserPayload payload) => _tokens[token] = payload;

        public Task<GoogleUserPayload?> ValidateAsync(string idToken, CancellationToken ct)
        {
            _tokens.TryGetValue(idToken, out var payload);
            return Task.FromResult(payload);
        }
    }
}
