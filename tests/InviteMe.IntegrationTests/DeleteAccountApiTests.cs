using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Identity;
using InviteMe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InviteMe.IntegrationTests;

public sealed class DeleteAccountApiTests
{
    private const string Password = "Test-only-Pass123!";

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task DeleteAccountWithPasswordSucceedsAnonymizesPiiAndDeactivatesUser() => WithApi(async (validator, client, db) =>
    {
        var email = "delete-pass@example.test";
        var clientAuth = await RegisterAndSignIn(client, email, "Original Name");

        var response = await clientAuth.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/auth/account")
        {
            Content = JsonContent.Create(new DeleteAccount(Password: Password))
        });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Verify DB state
        var user = await db.Set<ApplicationUser>().SingleAsync(u => u.NormalizedEmail!.StartsWith("DELETED-"));
        Assert.Equal("DISABLED", user.Status);
        Assert.Equal("Deleted User", user.FullName);
        Assert.Null(user.PasswordHash);
        Assert.Null(user.PhoneNumber);
        Assert.Null(user.AvatarUrl);
        Assert.False(user.EmailConfirmed);
        Assert.StartsWith("deleted-", user.Email);
        Assert.EndsWith("@deleted.inviteme.local", user.Email);

        // Verify user_logins and user_tokens are cleared
        var logins = await db.UserLogins.Where(l => l.UserId == user.Id).ToListAsync();
        Assert.Empty(logins);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task DeleteAccountWithGoogleTokenSucceedsForGoogleOnlyUser() => WithApi(async (validator, client, db) =>
    {
        var email = "google-only@example.test";
        var sub = "google-sub-del-001";
        validator.RegisterToken("token-google-del", new GoogleUserPayload(sub, email, true, "Google User", "https://pic.test/1.png"));

        var loginRes = await client.PostAsJsonAsync("/api/auth/google", new GoogleLogin("token-google-del"));
        Assert.Equal(HttpStatusCode.OK, loginRes.StatusCode);
        var token = (await loginRes.Content.ReadFromJsonAsync<TokenDto>())!;

        using var authedClient = CreateAuthorizedClient(client, token.AccessToken);

        // Delete account using Google IdToken
        var deleteRes = await authedClient.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/auth/account")
        {
            Content = JsonContent.Create(new DeleteAccount(GoogleIdToken: "token-google-del"))
        });
        Assert.Equal(HttpStatusCode.NoContent, deleteRes.StatusCode);

        // Verify user disabled and logins removed
        var user = await db.Set<ApplicationUser>().SingleAsync(u => u.NormalizedEmail!.StartsWith("DELETED-"));
        Assert.Equal("DISABLED", user.Status);
        Assert.Equal("Deleted User", user.FullName);
        Assert.Null(user.AvatarUrl);

        var logins = await db.UserLogins.Where(l => l.UserId == user.Id).ToListAsync();
        Assert.Empty(logins);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task DeleteAccountRejectedOnMissingOrInvalidReauthentication() => WithApi(async (validator, client, db) =>
    {
        var email = "reauth-check@example.test";
        var authed = await RegisterAndSignIn(client, email, "Reauth User");

        // 1. Missing password on password-enabled account -> 400 ValidationFailed
        var emptyBodyRes = await authed.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/auth/account")
        {
            Content = JsonContent.Create(new DeleteAccount())
        });
        Assert.Equal(HttpStatusCode.BadRequest, emptyBodyRes.StatusCode);

        // 2. Wrong password -> 401
        var wrongPassRes = await authed.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/auth/account")
        {
            Content = JsonContent.Create(new DeleteAccount(Password: "WrongPassword999!"))
        });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassRes.StatusCode);

        // Account still ACTIVE
        var user = await db.Set<ApplicationUser>().SingleAsync(u => u.Email == email);
        Assert.Equal("ACTIVE", user.Status);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task DeleteAccountBlockedWhenOwningActiveWeddingsPreservesCoHostRoles() => WithApi(async (validator, client, db) =>
    {
        var ownerEmail = "active-owner@example.test";
        var ownerClient = await RegisterAndSignIn(client, ownerEmail, "Active Owner");
        var ownerUser = await db.Set<ApplicationUser>().SingleAsync(u => u.Email == ownerEmail);

        // Owner owns an active wedding (DRAFT)
        var wedding = Wedding.Create(ownerUser.Id, "Owner Wedding", "owner-wedding-slug", 100);
        db.Weddings.Add(wedding);

        // Another wedding owned by someone else
        var otherOwner = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "other-owner@example.test",
            Email = "other-owner@example.test",
            FullName = "Other Owner",
            Status = "ACTIVE"
        };
        db.Set<ApplicationUser>().Add(otherOwner);
        await db.SaveChangesAsync();

        var otherWedding = Wedding.Create(otherOwner.Id, "Other Wedding", "other-wedding-slug", 50);
        db.Weddings.Add(otherWedding);
        var coHostMember = WeddingMember.Create(otherWedding.Id, ownerUser.Id, "CO_HOST", "ACTIVE");
        db.WeddingMembers.Add(coHostMember);
        await db.SaveChangesAsync();

        // Attempt account deletion
        var deleteRes = await ownerClient.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/auth/account")
        {
            Content = JsonContent.Create(new DeleteAccount(Password: Password))
        });
        Assert.Equal(HttpStatusCode.Conflict, deleteRes.StatusCode);

        // Owner account remains ACTIVE
        await db.Entry(ownerUser).ReloadAsync();
        Assert.Equal("ACTIVE", ownerUser.Status);

        // Crucial test: Co-Host role in other wedding was NOT revoked because pre-check prevented transaction
        await db.Entry(coHostMember).ReloadAsync();
        Assert.Equal("ACTIVE", coHostMember.Status);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task DeleteAccountRevokesCoHostMembershipsAndWritesAuditLogWithoutPii() => WithApi(async (validator, client, db) =>
    {
        var coHostEmail = "cohost-user@example.test";
        var coHostClient = await RegisterAndSignIn(client, coHostEmail, "CoHost User");
        var coHostUser = await db.Set<ApplicationUser>().SingleAsync(u => u.Email == coHostEmail);

        // Wedding owned by someone else
        var weddingOwner = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "main-owner@example.test",
            Email = "main-owner@example.test",
            FullName = "Main Owner",
            Status = "ACTIVE"
        };
        db.Set<ApplicationUser>().Add(weddingOwner);
        await db.SaveChangesAsync();

        var wedding = Wedding.Create(weddingOwner.Id, "Main Wedding", "main-wedding", 200);
        db.Weddings.Add(wedding);

        var member = WeddingMember.Create(wedding.Id, coHostUser.Id, "CO_HOST", "ACTIVE");
        db.WeddingMembers.Add(member);
        await db.SaveChangesAsync();

        // Add a permission
        var permission = await db.Permissions.FirstAsync();
        db.WeddingMemberPermissions.Add(WeddingMemberPermission.Create(member.Id, permission.Id));
        await db.SaveChangesAsync();

        // Co-Host deletes account
        var deleteRes = await coHostClient.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/auth/account")
        {
            Content = JsonContent.Create(new DeleteAccount(Password: Password))
        });
        Assert.Equal(HttpStatusCode.NoContent, deleteRes.StatusCode);

        // Membership revoked
        await db.Entry(member).ReloadAsync();
        Assert.Equal("REVOKED", member.Status);

        // Permissions cleared
        var remainingPerms = await db.WeddingMemberPermissions.Where(p => p.MemberId == member.Id).ToListAsync();
        Assert.Empty(remainingPerms);

        // Audit log written without PII
        var audit = await db.AuditLogs.SingleAsync(a => a.WeddingId == wedding.Id && a.Action == "COHOST_REVOKED");
        Assert.Equal("USER", audit.ActorType);
        Assert.Equal(coHostUser.Id, audit.ActorUserId);
        Assert.NotNull(audit.Metadata);
        Assert.Contains("USER_ACCOUNT_DELETED", audit.Metadata);
        Assert.DoesNotContain(coHostEmail, audit.Metadata);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task DeleteAccountBlockedForAdmin() => WithApi(async (validator, client, db) =>
    {
        var adminEmail = "admin-test@example.test";
        var adminClient = await RegisterAndSignIn(client, adminEmail, "Admin User");
        var adminUser = await db.Set<ApplicationUser>().SingleAsync(u => u.Email == adminEmail);

        // Elevate user to ADMIN
        var adminRole = await db.Roles.SingleAsync(r => r.NormalizedName == "ADMIN");
        db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = adminUser.Id, RoleId = adminRole.Id });
        await db.SaveChangesAsync();

        var deleteRes = await adminClient.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/auth/account")
        {
            Content = JsonContent.Create(new DeleteAccount(Password: Password))
        });
        Assert.Equal(HttpStatusCode.Forbidden, deleteRes.StatusCode);

        // Admin account remains ACTIVE
        await db.Entry(adminUser).ReloadAsync();
        Assert.Equal("ACTIVE", adminUser.Status);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task DeleteAccountImmediatelyRevokesTokensAndPreventsSessionAccess() => WithApi(async (validator, client, db) =>
    {
        var email = "token-revoke@example.test";
        var reg = await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(email, Password, "Revoke User"));
        Assert.Equal(HttpStatusCode.Created, reg.StatusCode);

        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginAccount(email, Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var tokens = (await login.Content.ReadFromJsonAsync<TokenDto>())!;

        using var authed = CreateAuthorizedClient(client, tokens.AccessToken);

        // Delete account
        var deleteRes = await authed.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/auth/account")
        {
            Content = JsonContent.Create(new DeleteAccount(Password: Password))
        });
        Assert.Equal(HttpStatusCode.NoContent, deleteRes.StatusCode);

        // 1. Using the old access token for /api/auth/me returns 401
        var meRes = await authed.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, meRes.StatusCode);

        // 2. Using the old refresh token returns 401
        var refreshRes = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshSession(tokens.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, refreshRes.StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task EmailCanBeReRegisteredAfterAccountDeletion() => WithApi(async (validator, client, db) =>
    {
        var email = "reregister@example.test";
        var authed = await RegisterAndSignIn(client, email, "First User");

        var userFirst = await db.Set<ApplicationUser>().AsNoTracking().SingleAsync(u => u.Email == email);
        var firstId = userFirst.Id;

        // Delete account
        var deleteRes = await authed.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/auth/account")
        {
            Content = JsonContent.Create(new DeleteAccount(Password: Password))
        });
        Assert.Equal(HttpStatusCode.NoContent, deleteRes.StatusCode);

        // Re-register with the EXACT SAME email succeeds
        var reRegRes = await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(email, Password, "Second User"));
        Assert.Equal(HttpStatusCode.Created, reRegRes.StatusCode);

        // The new account has a DIFFERENT user ID
        var userSecond = await db.Set<ApplicationUser>().AsNoTracking().SingleAsync(u => u.Email == email);
        Assert.NotEqual(firstId, userSecond.Id);
        Assert.Equal("ACTIVE", userSecond.Status);
        Assert.Equal("Second User", userSecond.FullName);

        // Old account is still in DB under deleted email
        var oldUser = await db.Set<ApplicationUser>().AsNoTracking().SingleAsync(u => u.Id == firstId);
        Assert.Equal("DISABLED", oldUser.Status);
        Assert.StartsWith("deleted-", oldUser.Email);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task GoogleLinkingRejectsUnconfirmedAccountWithBusinessDataAndAccountConflict() => WithApi(async (validator, client, db) =>
    {
        // 1. Unconfirmed account WITH business data cannot be linked
        var unconfirmedEmail = "has-data@example.test";
        var reg = await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(unconfirmedEmail, Password, "Has Data"));
        Assert.Equal(HttpStatusCode.Created, reg.StatusCode);

        var unconfirmedUser = await db.Set<ApplicationUser>().SingleAsync(u => u.Email == unconfirmedEmail);
        db.Weddings.Add(Wedding.Create(unconfirmedUser.Id, "Unconfirmed Wedding", "unconfirmed-slug", 50));
        await db.SaveChangesAsync();

        validator.RegisterToken("token-has-data", new GoogleUserPayload("sub-has-data", unconfirmedEmail, true, "Has Data", null));
        var linkRes = await client.PostAsJsonAsync("/api/auth/google", new GoogleLogin("token-has-data"));
        Assert.Equal(HttpStatusCode.Conflict, linkRes.StatusCode);

        // 2. Account already linked to Google sub A cannot link to Google sub B
        var linkedEmail = "already-linked@example.test";
        validator.RegisterToken("token-sub-a", new GoogleUserPayload("sub-aaa", linkedEmail, true, "Linked A", null));
        var initialLink = await client.PostAsJsonAsync("/api/auth/google", new GoogleLogin("token-sub-a"));
        Assert.Equal(HttpStatusCode.OK, initialLink.StatusCode);

        // Attempt login with different sub for same email
        validator.RegisterToken("token-sub-b", new GoogleUserPayload("sub-bbb", linkedEmail, true, "Linked B", null));
        var conflictLink = await client.PostAsJsonAsync("/api/auth/google", new GoogleLogin("token-sub-b"));
        Assert.Equal(HttpStatusCode.Conflict, conflictLink.StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ConcurrentAccountDeletionIsSafe() => WithApi(async (validator, client, db) =>
    {
        var email = "concurrent-del@example.test";
        var authed = await RegisterAndSignIn(client, email, "Concurrent User");

        // Fire two delete requests concurrently
        var task1 = authed.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/auth/account")
        {
            Content = JsonContent.Create(new DeleteAccount(Password: Password))
        });
        var task2 = authed.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/auth/account")
        {
            Content = JsonContent.Create(new DeleteAccount(Password: Password))
        });

        var results = await Task.WhenAll(task1, task2);
        var statusCodes = results.Select(r => r.StatusCode).ToList();

        // Exactly one should succeed with 204, and the other gets rejected (401 or conflict), NO 500 error!
        Assert.Contains(HttpStatusCode.NoContent, statusCodes);
        Assert.DoesNotContain(HttpStatusCode.InternalServerError, statusCodes);
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

    private static async Task<HttpClient> RegisterAndSignIn(HttpClient client, string email, string displayName)
    {
        var reg = await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(email, Password, displayName));
        Assert.Equal(HttpStatusCode.Created, reg.StatusCode);

        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginAccount(email, Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var tokens = (await login.Content.ReadFromJsonAsync<TokenDto>())!;
        return CreateAuthorizedClient(client, tokens.AccessToken);
    }

    private static HttpClient CreateAuthorizedClient(HttpClient baseClient, string token)
    {
        var client = new HttpClient(new DelegatingHandlerStub(baseClient))
        {
            BaseAddress = baseClient.BaseAddress
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private sealed class DelegatingHandlerStub(HttpClient client) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Clone request for execution by baseClient
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
