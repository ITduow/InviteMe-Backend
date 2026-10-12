using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Guests.Import;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Weddings.Workspace;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.IntegrationTests;

public sealed class TenantIsolationTests
{
    private const string Password = "Test-only-Pass123!";

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task MultiTenantWeddingIsolationProtectsAllResourcesFromCrossTenantAccess() =>
        WithApi(async (db, factory) =>
        {
            using var userAlpha = await SignIn(factory, "user_alpha");
            using var userBeta = await SignIn(factory, "user_beta");

            var weddingAlpha = await CreateWedding(userAlpha, "wedding-alpha");
            var weddingBeta = await CreateWedding(userBeta, "wedding-beta");

            var alphaSettings = await userAlpha.GetAsync($"/api/weddings/{weddingAlpha.Id}/settings");
            Assert.Equal(HttpStatusCode.OK, alphaSettings.StatusCode);

            var betaSettings = await userBeta.GetAsync($"/api/weddings/{weddingBeta.Id}/settings");
            Assert.Equal(HttpStatusCode.OK, betaSettings.StatusCode);

            var alphaList = await userAlpha.GetFromJsonAsync<WeddingPage>("/api/weddings?page=1&pageSize=20");
            Assert.NotNull(alphaList);
            Assert.Contains(alphaList.Items, x => x.Id == weddingAlpha.Id);
            Assert.DoesNotContain(alphaList.Items, x => x.Id == weddingBeta.Id);

            var betaList = await userBeta.GetFromJsonAsync<WeddingPage>("/api/weddings?page=1&pageSize=20");
            Assert.NotNull(betaList);
            Assert.Contains(betaList.Items, x => x.Id == weddingBeta.Id);
            Assert.DoesNotContain(betaList.Items, x => x.Id == weddingAlpha.Id);

            var originalBeta = await db.Weddings.AsNoTracking().SingleAsync(x => x.Id == weddingBeta.Id);

            var forbiddenEndpoints = new (HttpMethod Method, string Path)[]
            {
                (HttpMethod.Get, $"/api/weddings/{weddingBeta.Id}"),
                (HttpMethod.Get, $"/api/weddings/{weddingBeta.Id}/settings"),
                (HttpMethod.Get, $"/api/weddings/{weddingBeta.Id}/access"),
                (HttpMethod.Get, $"/api/weddings/{weddingBeta.Id}/guests"),
                (HttpMethod.Get, $"/api/weddings/{weddingBeta.Id}/invitations"),
                (HttpMethod.Get, $"/api/weddings/{weddingBeta.Id}/tables"),
                (HttpMethod.Get, $"/api/weddings/{weddingBeta.Id}/seating"),
                (HttpMethod.Get, $"/api/weddings/{weddingBeta.Id}/check-ins"),
                (HttpMethod.Get, $"/api/weddings/{weddingBeta.Id}/gifts"),
                (HttpMethod.Get, $"/api/weddings/{weddingBeta.Id}/reports/summary")
            };

            foreach (var (method, path) in forbiddenEndpoints)
            {
                var request = new HttpRequestMessage(method, path);
                var response = await userAlpha.SendAsync(request);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }

            var tamperInput = new WorkspaceInput(
                "Hacked Wedding Title",
                "hacked-slug",
                MaxCapacity: 999,
                Timezone: "Asia/Ho_Chi_Minh",
                ExpectedVersion: weddingBeta.Version);

            var tamperResponse = await userAlpha.PutAsJsonAsync(
                $"/api/weddings/{weddingBeta.Id}/settings",
                tamperInput);

            Assert.Equal(HttpStatusCode.Forbidden, tamperResponse.StatusCode);

            var guestInput = new GuestInput("HACK01", "Hacked Guest", Email: "hacker@example.test");
            var injectResponse = await userAlpha.PostAsJsonAsync(
                $"/api/weddings/{weddingBeta.Id}/guests",
                guestInput);

            Assert.Equal(HttpStatusCode.Forbidden, injectResponse.StatusCode);

            var currentBeta = await db.Weddings.AsNoTracking().SingleAsync(x => x.Id == weddingBeta.Id);
            Assert.Equal(originalBeta.Title, currentBeta.Title);
            Assert.Equal(originalBeta.Slug, currentBeta.Slug);
            Assert.Equal(originalBeta.Version, currentBeta.Version);
            Assert.Equal(0, await db.WeddingGuests.CountAsync(x => x.WeddingId == weddingBeta.Id));
            Assert.Equal(0, await db.Gifts.CountAsync(x => x.WeddingId == weddingBeta.Id));
        });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task CohostMembershipEnforcesGranularPermissionsAndRevocation() =>
        WithApi(async (db, factory) =>
        {
            using var ownerBeta = await SignIn(factory, "owner_beta");
            using var cohostCharlie = await SignIn(factory, "cohost_charlie");
            using var outsider = await SignIn(factory, "outsider_dan");

            var weddingBeta = await CreateWedding(ownerBeta, "beta-workspace");
            var charlieId = (await cohostCharlie.GetFromJsonAsync<AccountDto>("/api/auth/me"))!.Id;

            var preCheck = await cohostCharlie.GetAsync($"/api/weddings/{weddingBeta.Id}/settings");
            Assert.Equal(HttpStatusCode.Forbidden, preCheck.StatusCode);

            var memberId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO inviteme.wedding_members(id, wedding_id, user_id, status) VALUES ({memberId}, {weddingBeta.Id}, {charlieId}, 'ACTIVE')");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO inviteme.wedding_member_permissions(member_id, permission_id) SELECT {memberId}, id FROM inviteme.permissions WHERE code='WEDDING_VIEW'");

            var viewResponse = await cohostCharlie.GetAsync($"/api/weddings/{weddingBeta.Id}/settings");
            Assert.Equal(HttpStatusCode.OK, viewResponse.StatusCode);

            var editInput = new WorkspaceInput("Charlie Title", "beta-workspace", MaxCapacity: null, Timezone: "Asia/Ho_Chi_Minh", ExpectedVersion: 1);
            var editResponse = await cohostCharlie.PutAsJsonAsync($"/api/weddings/{weddingBeta.Id}/settings", editInput);
            Assert.Equal(HttpStatusCode.Forbidden, editResponse.StatusCode);

            var reportResponse = await cohostCharlie.GetAsync($"/api/weddings/{weddingBeta.Id}/reports/summary?includeGifts=true");
            Assert.Equal(HttpStatusCode.Forbidden, reportResponse.StatusCode);

            var outsiderCheck = await outsider.GetAsync($"/api/weddings/{weddingBeta.Id}/settings");
            Assert.Equal(HttpStatusCode.Forbidden, outsiderCheck.StatusCode);

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE inviteme.wedding_members SET status='REVOKED' WHERE id={memberId}");

            var revokedResponse = await cohostCharlie.GetAsync($"/api/weddings/{weddingBeta.Id}/settings");
            Assert.Equal(HttpStatusCode.Forbidden, revokedResponse.StatusCode);
        });

    private static Task WithApi(Func<InviteMeDbContext, WorkspaceApiFactory, Task> action) =>
        SchemaV2MigrationTests.InDatabase(async db =>
        {
            await db.Database.MigrateAsync();
            await using var factory = new WorkspaceApiFactory(db.Database.GetConnectionString()!);
            await action(db, factory);
        });

    private static async Task<HttpClient> SignIn(WorkspaceApiFactory factory, string name)
    {
        var client = factory.CreateHttpsClient();
        var email = $"{name}@example.test";

        var register = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterAccount(email, Password, name));
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginAccount(email, Password));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var token = (await login.Content.ReadFromJsonAsync<TokenDto>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        return client;
    }

    private static async Task<WorkspaceDto> CreateWedding(HttpClient client, string slug)
    {
        var input = new WorkspaceInput(
            Title: $"Wedding {slug}",
            Slug: slug,
            MaxCapacity: 100,
            Timezone: "Asia/Ho_Chi_Minh");

        var response = await client.PostAsJsonAsync("/api/weddings", input);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<WorkspaceDto>())!;
    }
}
