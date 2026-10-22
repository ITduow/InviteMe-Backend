using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Weddings.CoHosts;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.IntegrationTests;

public sealed class CoHostPermissionTests
{
    private const string Password = "Test-only-Pass123!";

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task OwnerOnlyPoliciesBlockCoHostsAndNonMembers() => WithApi(async (db, factory) =>
    {
        using var owner = await SignIn(factory, "owner2");
        using var cohost = await SignIn(factory, "cohost2");
        using var outsider = await SignIn(factory, "outsider2");

        var wedding = await CreateWedding(db, owner, "coh-security");
        var cohostAccount = (await cohost.GetFromJsonAsync<AccountDto>("/api/auth/me"))!;
        var ownerAccount = (await owner.GetFromJsonAsync<AccountDto>("/api/auth/me"))!;

        await AddCoHostAsync(owner, cohost, wedding.Id, "GUEST_VIEW");

        Assert.Equal(HttpStatusCode.Forbidden, (await cohost.GetAsync($"/api/weddings/{wedding.Id}/co-hosts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/weddings/{wedding.Id}/co-hosts")).StatusCode);

        var inviteInput = new CreateCoHostInvitationInput("new@example.test", [new("GUEST_VIEW", [], true)]);
        Assert.Equal(HttpStatusCode.Forbidden, (await cohost.PostAsJsonAsync($"/api/weddings/{wedding.Id}/co-host-invitations", inviteInput)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync($"/api/weddings/{wedding.Id}/co-host-invitations", inviteInput)).StatusCode);

        var scopeInput = new UpdateCoHostScopesInput([new("GUEST_EDIT", [], true)]);
        Assert.Equal(HttpStatusCode.Forbidden, (await cohost.PutAsJsonAsync($"/api/weddings/{wedding.Id}/co-hosts/{cohostAccount.Id}/scopes", scopeInput)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PutAsJsonAsync($"/api/weddings/{wedding.Id}/co-hosts/{cohostAccount.Id}/scopes", scopeInput)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await cohost.DeleteAsync($"/api/weddings/{wedding.Id}/co-hosts/{cohostAccount.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.DeleteAsync($"/api/weddings/{wedding.Id}/co-hosts/{cohostAccount.Id}")).StatusCode);

        var selfInvite = await owner.PostAsJsonAsync($"/api/weddings/{wedding.Id}/co-host-invitations",
            new CreateCoHostInvitationInput(ownerAccount.Email, [new("GUEST_VIEW", [], true)]));
        Assert.Equal(HttpStatusCode.Conflict, selfInvite.StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ValidationAndReactivationBehaviorsWorkCorrectly() => WithApi(async (db, factory) =>
    {
        using var owner = await SignIn(factory, "owner3");
        using var cohost = await SignIn(factory, "cohost3");

        var wedding = await CreateWedding(db, owner, "coh-validation");
        var cohostAccount = (await cohost.GetFromJsonAsync<AccountDto>("/api/auth/me"))!;
        var invitationsUrl = $"/api/weddings/{wedding.Id}/co-host-invitations";

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(invitationsUrl,
            new CreateCoHostInvitationInput("invalid-email", [new("GUEST_VIEW", [], true)]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(invitationsUrl,
            new CreateCoHostInvitationInput(cohostAccount.Email, [new("INVALID_PERMISSION", [], true)]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(invitationsUrl,
            new CreateCoHostInvitationInput(cohostAccount.Email, [new("GUEST_VIEW", [], true), new("GUEST_VIEW", [], true)]))).StatusCode);

        await AddCoHostAsync(owner, cohost, wedding.Id, "GUEST_VIEW");

        var duplicateInvite = await owner.PostAsJsonAsync(invitationsUrl,
            new CreateCoHostInvitationInput(cohostAccount.Email, [new("GUEST_VIEW", [], true)]));
        Assert.Equal(HttpStatusCode.Conflict, duplicateInvite.StatusCode);

        await owner.DeleteAsync($"/api/weddings/{wedding.Id}/co-hosts/{cohostAccount.Id}");

        var reactivated = await AddCoHostAsync(owner, cohost, wedding.Id, "RSVP_VIEW", "CHECKIN_MANAGE");
        Assert.Equal("ACTIVE", reactivated.Status);
        Assert.Equal(2, reactivated.Permissions.Count);
        Assert.Contains("RSVP_VIEW", reactivated.Permissions);
        Assert.Contains("CHECKIN_MANAGE", reactivated.Permissions);
    });

    // Permissions an owner can delegate to a co-host (CoHostPermissionCodes): WEDDING_VIEW comes with the membership,
    // wedding editing, invitations, gifts and analytics stay with the owner.
    internal static readonly string[] Delegable = ["GUEST_VIEW", "GUEST_EDIT", "RSVP_VIEW", "SEATING_VIEW", "SEATING_EDIT", "CHECKIN_MANAGE"];

    internal static Task WithApi(Func<InviteMeDbContext, DatabaseApiFactory, Task> action) =>
        SchemaV2MigrationTests.InDatabase(async db =>
        {
            await db.Database.MigrateAsync();
            await using var factory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
            await action(db, factory);
        });

    internal static async Task<InviteMe.Domain.Weddings.Wedding> CreateWedding(InviteMeDbContext db, HttpClient client, string slug)
    {
        var owner = (await client.GetFromJsonAsync<AccountDto>("/api/auth/me"))!;
        var wedding = InviteMe.Domain.Weddings.Wedding.Create(owner.Id, "Co-Host Test Wedding", slug, 100);
        db.Weddings.Add(wedding);
        await db.SaveChangesAsync();
        return wedding;
    }

    internal static async Task<CoHostDto> AddCoHostAsync(HttpClient owner, HttpClient cohost, Guid weddingId, params string[] permissions)
    {
        var cohostAccount = (await cohost.GetFromJsonAsync<AccountDto>("/api/auth/me"))!;
        var grants = (permissions.Length == 0 ? ["GUEST_VIEW"] : permissions)
            .Select(p => new CoHostPermissionScopeInput(p, [], true))
            .ToArray();
        var invite = await owner.PostAsJsonAsync($"/api/weddings/{weddingId}/co-host-invitations",
            new CreateCoHostInvitationInput(cohostAccount.Email, grants));
        Assert.Equal(HttpStatusCode.Accepted, invite.StatusCode);
        var token = DatabaseApiFactory.GetInvitationToken(cohostAccount.Email);
        var accept = await cohost.PostAsJsonAsync("/api/co-host-invitations/accept", new { Token = token });
        Assert.Equal(HttpStatusCode.NoContent, accept.StatusCode);
        if (permissions.Length == 0)
        {
            var clear = await owner.PutAsJsonAsync($"/api/weddings/{weddingId}/co-hosts/{cohostAccount.Id}/scopes", new UpdateCoHostScopesInput([]));
            Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        }
        return (await owner.GetFromJsonAsync<CoHostDto>($"/api/weddings/{weddingId}/co-hosts/{cohostAccount.Id}"))!;
    }

    internal static async Task<HttpClient> SignIn(DatabaseApiFactory factory, string prefix)
    {
        var client = factory.CreateHttpsClient();
        var email = $"{prefix}-{Guid.NewGuid():N}@example.test";
        var register = await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(email, Password, prefix));
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginAccount(email, Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var tokens = (await login.Content.ReadFromJsonAsync<TokenDto>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }
}
