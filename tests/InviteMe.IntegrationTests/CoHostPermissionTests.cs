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

        var wedding = await CreateWedding(db, owner, "coh-validation");
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
        var wedding = InviteMe.Domain.Weddings.Wedding.Create(owner.Id, "CoHost Test Wedding", slug, 100);
        db.Weddings.Add(wedding);
        await db.SaveChangesAsync();
        return wedding;
    }

    internal static async Task<HttpClient> SignIn(DatabaseApiFactory factory, string name)
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
