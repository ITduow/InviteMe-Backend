using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using InviteMe.Application.Features.Guests.Import;
using InviteMe.Application.Features.Guests.ListWeddingGuests;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Weddings.CoHosts;
using InviteMe.Application.Features.Weddings.Workspace;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.IntegrationTests;

public sealed class CoHostPermissionTests
{
    private const string Password = "Test-only-Pass123!";

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task FullCoHostLifecycleEnforcesGranularPermissionsAndInstantRevocation() => WithApi(async (db, factory) =>
    {
        using var owner = await SignIn(factory, "owner");
        using var cohost = await SignIn(factory, "cohost");
        using var outsider = await SignIn(factory, "outsider");

        var wedding = await CreateWedding(owner, "coh-lifecycle");
        var cohostAccount = (await cohost.GetFromJsonAsync<AccountDto>("/api/auth/me"))!;

        var addPayload = new AddCoHostInput(cohostAccount.Email, ["GUEST_VIEW", "SEATING_VIEW"]);
        var addResponse = await owner.PostAsJsonAsync($"/api/weddings/{wedding.Id}/co-hosts", addPayload);
        Assert.Equal(HttpStatusCode.Created, addResponse.StatusCode);
        var added = (await addResponse.Content.ReadFromJsonAsync<CoHostDto>())!;
        Assert.Equal(cohostAccount.Id, added.UserId);
        Assert.Equal("ACTIVE", added.Status);
        Assert.Equal(2, added.Permissions.Count);
        Assert.Contains("GUEST_VIEW", added.Permissions);
        Assert.Contains("SEATING_VIEW", added.Permissions);

        var listResponse = await owner.GetFromJsonAsync<List<CoHostDto>>($"/api/weddings/{wedding.Id}/co-hosts");
        Assert.NotNull(listResponse);
        var listed = Assert.Single(listResponse);
        Assert.Equal(cohostAccount.Id, listed.UserId);

        var accessResponse = await cohost.GetFromJsonAsync<WeddingAccessDto>($"/api/weddings/{wedding.Id}/access");
        Assert.NotNull(accessResponse);
        Assert.Equal(2, accessResponse.Permissions.Count);
        Assert.Contains("GUEST_VIEW", accessResponse.Permissions);
        Assert.Contains("SEATING_VIEW", accessResponse.Permissions);

        var guestListResponse = await cohost.GetAsync($"/api/weddings/{wedding.Id}/guests");
        Assert.Equal(HttpStatusCode.OK, guestListResponse.StatusCode);

        var unauthorizedEditResponse = await cohost.PostAsJsonAsync($"/api/weddings/{wedding.Id}/guests", new GuestInput("G01", "Guest One"));
        Assert.Equal(HttpStatusCode.Forbidden, unauthorizedEditResponse.StatusCode);

        var updatePayload = new UpdateCoHostPermissionsInput(["GUEST_VIEW", "GUEST_EDIT"]);
        var updateResponse = await owner.PutAsJsonAsync($"/api/weddings/{wedding.Id}/co-hosts/{cohostAccount.Id}/permissions", updatePayload);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = (await updateResponse.Content.ReadFromJsonAsync<CoHostDto>())!;
        Assert.Contains("GUEST_EDIT", updated.Permissions);
        Assert.DoesNotContain("SEATING_VIEW", updated.Permissions);

        var authorizedEditResponse = await cohost.PostAsJsonAsync($"/api/weddings/{wedding.Id}/guests", new GuestInput("G01", "Guest One"));
        Assert.Equal(HttpStatusCode.Created, authorizedEditResponse.StatusCode);

        var revokeResponse = await owner.DeleteAsync($"/api/weddings/{wedding.Id}/co-hosts/{cohostAccount.Id}");
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        var revokedGuestAccess = await cohost.GetAsync($"/api/weddings/{wedding.Id}/guests");
        Assert.Equal(HttpStatusCode.Forbidden, revokedGuestAccess.StatusCode);

        var revokedAccessCheck = await cohost.GetAsync($"/api/weddings/{wedding.Id}/access");
        Assert.Equal(HttpStatusCode.Forbidden, revokedAccessCheck.StatusCode);

        var memberInDb = await db.WeddingMembers.SingleOrDefaultAsync(x => x.WeddingId == wedding.Id && x.UserId == cohostAccount.Id);
        Assert.NotNull(memberInDb);
        Assert.Equal("REVOKED", memberInDb.Status);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task OwnerOnlyPoliciesBlockCoHostsAndNonMembers() => WithApi(async (db, factory) =>
    {
        using var owner = await SignIn(factory, "owner2");
        using var cohost = await SignIn(factory, "cohost2");
        using var outsider = await SignIn(factory, "outsider2");

        var wedding = await CreateWedding(owner, "coh-security");
        var cohostAccount = (await cohost.GetFromJsonAsync<AccountDto>("/api/auth/me"))!;
        var ownerAccount = (await owner.GetFromJsonAsync<AccountDto>("/api/auth/me"))!;

        await owner.PostAsJsonAsync($"/api/weddings/{wedding.Id}/co-hosts", new AddCoHostInput(cohostAccount.Email, ["GUEST_VIEW"]));

        Assert.Equal(HttpStatusCode.Forbidden, (await cohost.GetAsync($"/api/weddings/{wedding.Id}/co-hosts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/weddings/{wedding.Id}/co-hosts")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await cohost.PostAsJsonAsync($"/api/weddings/{wedding.Id}/co-hosts", new AddCoHostInput("new@example.test", ["GUEST_VIEW"]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync($"/api/weddings/{wedding.Id}/co-hosts", new AddCoHostInput("new@example.test", ["GUEST_VIEW"]))).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await cohost.PutAsJsonAsync($"/api/weddings/{wedding.Id}/co-hosts/{cohostAccount.Id}/permissions", new UpdateCoHostPermissionsInput(["GUEST_EDIT"]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PutAsJsonAsync($"/api/weddings/{wedding.Id}/co-hosts/{cohostAccount.Id}/permissions", new UpdateCoHostPermissionsInput(["GUEST_EDIT"]))).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await cohost.DeleteAsync($"/api/weddings/{wedding.Id}/co-hosts/{cohostAccount.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.DeleteAsync($"/api/weddings/{wedding.Id}/co-hosts/{cohostAccount.Id}")).StatusCode);

        var selfAdd = await owner.PostAsJsonAsync($"/api/weddings/{wedding.Id}/co-hosts", new AddCoHostInput(ownerAccount.Email, ["GUEST_VIEW"]));
        Assert.Equal(HttpStatusCode.Conflict, selfAdd.StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ValidationAndReactivationBehaviorsWorkCorrectly() => WithApi(async (db, factory) =>
    {
        using var owner = await SignIn(factory, "owner3");
        using var cohost = await SignIn(factory, "cohost3");

        var wedding = await CreateWedding(owner, "coh-validation");
        var cohostAccount = (await cohost.GetFromJsonAsync<AccountDto>("/api/auth/me"))!;

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/weddings/{wedding.Id}/co-hosts", new AddCoHostInput("invalid-email", ["GUEST_VIEW"]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/weddings/{wedding.Id}/co-hosts", new AddCoHostInput(cohostAccount.Email, ["INVALID_PERMISSION"]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync($"/api/weddings/{wedding.Id}/co-hosts", new AddCoHostInput(cohostAccount.Email, ["GUEST_VIEW", "GUEST_VIEW"]))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync($"/api/weddings/{wedding.Id}/co-hosts", new AddCoHostInput("nonexistent@example.test", ["GUEST_VIEW"]))).StatusCode);

        var initialAdd = await owner.PostAsJsonAsync($"/api/weddings/{wedding.Id}/co-hosts", new AddCoHostInput(cohostAccount.Email, ["GUEST_VIEW"]));
        Assert.Equal(HttpStatusCode.Created, initialAdd.StatusCode);

        var duplicateAdd = await owner.PostAsJsonAsync($"/api/weddings/{wedding.Id}/co-hosts", new AddCoHostInput(cohostAccount.Email, ["GUEST_VIEW"]));
        Assert.Equal(HttpStatusCode.Conflict, duplicateAdd.StatusCode);

        await owner.DeleteAsync($"/api/weddings/{wedding.Id}/co-hosts/{cohostAccount.Id}");

        var reactivate = await owner.PostAsJsonAsync($"/api/weddings/{wedding.Id}/co-hosts", new AddCoHostInput(cohostAccount.Email, ["WEDDING_VIEW", "ANALYTICS_VIEW"]));
        Assert.Equal(HttpStatusCode.Created, reactivate.StatusCode);
        var reactivated = (await reactivate.Content.ReadFromJsonAsync<CoHostDto>())!;
        Assert.Equal("ACTIVE", reactivated.Status);
        Assert.Equal(2, reactivated.Permissions.Count);
        Assert.Contains("WEDDING_VIEW", reactivated.Permissions);
        Assert.Contains("ANALYTICS_VIEW", reactivated.Permissions);
    });

    internal static Task WithApi(Func<InviteMeDbContext, WorkspaceApiFactory, Task> action) =>
        SchemaV2MigrationTests.InDatabase(async db =>
        {
            await db.Database.MigrateAsync();
            await using var factory = new WorkspaceApiFactory(db.Database.GetConnectionString()!);
            await action(db, factory);
        });

    internal static async Task<WorkspaceDto> CreateWedding(HttpClient client, string slug)
    {
        var input = new WorkspaceInput("CoHost Test Wedding", slug, 100, "Asia/Ho_Chi_Minh");
        var response = await client.PostAsJsonAsync("/api/weddings", input);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<WorkspaceDto>())!;
    }

    internal static async Task<HttpClient> SignIn(WorkspaceApiFactory factory, string name)
    {
        var client = factory.CreateHttpsClient();
        var email = $"{name}@example.test";
        await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(email, Password, name));
        var tokenResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginAccount(email, Password));
        var token = (await tokenResponse.Content.ReadFromJsonAsync<TokenDto>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return client;
    }
}
