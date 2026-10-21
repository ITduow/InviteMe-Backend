using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Infrastructure.Identity;
using InviteMe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InviteMe.IntegrationTests;

public sealed class UpdateProfileApiTests
{
    private const string Password = "Test-only-Pass123!";

    [Fact]
    public async Task PutMeUpdatesDisplayNamePhoneAndAvatarUrlAndPersistsInMeEndpoint()
    {
        await using var factory = new ApiFactory();
        using var client = CreateAuthenticatedClient(factory, Guid.NewGuid());

        // 1. PUT /api/auth/me trims whitespace and updates displayName, phone, avatarUrl
        var putRes = await client.PutAsJsonAsync("/api/auth/me", new UpdateProfile(
            "  Nguyen Huu Anh  ",
            " 0901234567 ",
            " https://example.com/avatar.png "));
        Assert.Equal(HttpStatusCode.OK, putRes.StatusCode);

        var updated = (await putRes.Content.ReadFromJsonAsync<AccountDto>())!;
        Assert.Equal("Nguyen Huu Anh", updated.DisplayName);
        Assert.Equal("0901234567", updated.Phone);
        Assert.Equal("https://example.com/avatar.png", updated.AvatarUrl);
        Assert.Equal("test@example.com", updated.Email);
        Assert.Equal("USER", updated.Role);

        // 2. Subsequent GET /api/auth/me returns the updated profile
        var meRes = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, meRes.StatusCode);
        var me = (await meRes.Content.ReadFromJsonAsync<AccountDto>())!;
        Assert.Equal("Nguyen Huu Anh", me.DisplayName);
        Assert.Equal("0901234567", me.Phone);
        Assert.Equal("https://example.com/avatar.png", me.AvatarUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task PutMeRejectsEmptyOrWhitespaceDisplayNameWith400(string invalidName)
    {
        await using var factory = new ApiFactory();
        using var client = CreateAuthenticatedClient(factory, Guid.NewGuid());

        var putRes = await client.PutAsJsonAsync("/api/auth/me", new UpdateProfile(invalidName));
        Assert.Equal(HttpStatusCode.BadRequest, putRes.StatusCode);
    }

    [Fact]
    public async Task PutMeRejectsInvalidLengthsOrInvalidAvatarUrlWith400()
    {
        await using var factory = new ApiFactory();
        using var client = CreateAuthenticatedClient(factory, Guid.NewGuid());

        // 1. DisplayName > 150 chars
        var tooLongNameRes = await client.PutAsJsonAsync("/api/auth/me", new UpdateProfile(new string('A', 51)));
        Assert.Equal(HttpStatusCode.BadRequest, tooLongNameRes.StatusCode);

        // 2. Phone > 30 chars
        var tooLongPhoneRes = await client.PutAsJsonAsync("/api/auth/me", new UpdateProfile("Valid Name", "1234567890"));
        Assert.Equal(HttpStatusCode.BadRequest, tooLongPhoneRes.StatusCode);

        // 3. Invalid AvatarUrl (not http/https)
        var invalidAvatarRes = await client.PutAsJsonAsync("/api/auth/me", new UpdateProfile("Valid Name", "0901234567", "not-a-valid-url"));
        Assert.Equal(HttpStatusCode.BadRequest, invalidAvatarRes.StatusCode);
    }

    [Fact]
    public async Task AnonymousUpdateProfileReturns401()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateHttpsClient();

        var putRes = await client.PutAsJsonAsync("/api/auth/me", new UpdateProfile("New Name"));
        Assert.Equal(HttpStatusCode.Unauthorized, putRes.StatusCode);
        var problem = await putRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AUTHENTICATION_REQUIRED", problem.GetProperty("code").GetString());
    }

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task UpdateProfileViaPutTrimsAndPersistsAllFieldsWithoutRevokingSession() => WithApi(async (client, db) =>
    {
        var email = "profile-update@example.test";
        using var authed = await RegisterAndSignIn(client, email, "Original Name");

        var putRes = await authed.PutAsJsonAsync("/api/auth/me", new UpdateProfile(
            "  Nguyen Huu Anh  ",
            " 0901234567 ",
            " https://example.com/avatar.png "));
        Assert.Equal(HttpStatusCode.OK, putRes.StatusCode);

        var updated = (await putRes.Content.ReadFromJsonAsync<AccountDto>())!;
        Assert.Equal("Nguyen Huu Anh", updated.DisplayName);
        Assert.Equal("0901234567", updated.Phone);
        Assert.Equal("https://example.com/avatar.png", updated.AvatarUrl);
        Assert.Equal(email, updated.Email);
        Assert.Equal("USER", updated.Role);
        Assert.True(updated.EmailConfirmed);

        // Verify GET /api/auth/me reflects the new profile with the same access token
        var meRes = await authed.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, meRes.StatusCode);
        var me = (await meRes.Content.ReadFromJsonAsync<AccountDto>())!;
        Assert.Equal("Nguyen Huu Anh", me.DisplayName);
        Assert.Equal("0901234567", me.Phone);
        Assert.Equal("https://example.com/avatar.png", me.AvatarUrl);

        // Verify database state in inviteme.users (full_name, phone, avatar_url)
        var userInDb = await db.Set<ApplicationUser>().AsNoTracking().SingleAsync(u => u.Email == email);
        Assert.Equal("Nguyen Huu Anh", userInDb.FullName);
        Assert.Equal("0901234567", userInDb.PhoneNumber);
        Assert.Equal("https://example.com/avatar.png", userInDb.AvatarUrl);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task UpdateProfileRejectsInvalidFieldsAndLeavesDatabaseUnchanged() => WithApi(async (client, db) =>
    {
        var email = "profile-invalid@example.test";
        using var authed = await RegisterAndSignIn(client, email, "Valid Initial Name");

        // 1. Empty string
        var emptyRes = await authed.PutAsJsonAsync("/api/auth/me", new UpdateProfile(""));
        Assert.Equal(HttpStatusCode.BadRequest, emptyRes.StatusCode);

        // 2. Whitespace only
        var whitespaceRes = await authed.PutAsJsonAsync("/api/auth/me", new UpdateProfile("     "));
        Assert.Equal(HttpStatusCode.BadRequest, whitespaceRes.StatusCode);

        // 3. Exceeds 150 chars
        var tooLongRes = await authed.PutAsJsonAsync("/api/auth/me", new UpdateProfile(new string('A', 51)));
        Assert.Equal(HttpStatusCode.BadRequest, tooLongRes.StatusCode);

        // Verify DB still has the original name
        var userInDb = await db.Set<ApplicationUser>().AsNoTracking().SingleAsync(u => u.Email == email);
        Assert.Equal("Valid Initial Name", userInDb.FullName);
    });

    private static HttpClient CreateAuthenticatedClient(ApiFactory factory, Guid userId)
    {
        var client = factory.CreateHttpsClient();
        using var scope = factory.Services.CreateScope();
        var issuer = scope.ServiceProvider.GetRequiredService<IAccessTokenIssuer>();
        var token = issuer.Issue(new AccountIdentity(userId, "Test User", "test@example.com", "USER", ApiFactory.DefaultSessionStamp, true));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessTokenValue);
        return client;
    }

    private static Task WithApi(Func<HttpClient, InviteMeDbContext, Task> action) => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        await using var factory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
        using var client = factory.CreateHttpsClient();
        await action(client, db);
    });

    private static async Task<HttpClient> RegisterAndSignIn(HttpClient client, string email, string displayName)
    {
        var reg = await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(email, Password, displayName));
        Assert.Equal(HttpStatusCode.Created, reg.StatusCode);

        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginAccount(email, Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var tokens = (await login.Content.ReadFromJsonAsync<TokenDto>())!;
        var authed = new HttpClient(new DelegatingHandlerStub(client))
        {
            BaseAddress = client.BaseAddress
        };
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return authed;
    }

    private sealed class DelegatingHandlerStub(HttpClient client) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Content = request.Content
            };
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            return client.SendAsync(clone, cancellationToken);
        }
    }
}
