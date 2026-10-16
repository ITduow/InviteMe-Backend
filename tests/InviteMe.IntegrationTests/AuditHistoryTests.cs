using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Audit;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Weddings.CoHosts;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.IntegrationTests;

public sealed class AuditHistoryTests
{
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task OnlyTheAuthenticatedOwnerCanReadTheAuditHistory() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "audit-auth-owner");
        using var outsider = await CoHostPermissionTests.SignIn(factory, "audit-auth-outsider");
        using var cohost = await CoHostPermissionTests.SignIn(factory, "audit-auth-cohost");
        using var anonymous = factory.CreateHttpsClient();
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "audit-auth");
        var cohostAccount = await Me(cohost);
        var url = Url(wedding.Id);

        await AssertStatus(HttpStatusCode.Unauthorized, anonymous.GetAsync(url), "anonymous");
        await AssertStatus(HttpStatusCode.Forbidden, outsider.GetAsync(url), "outsider");
        await AssertStatus(HttpStatusCode.Forbidden, owner.GetAsync(Url(Guid.NewGuid())), "owner on missing wedding");
        await AssertStatus(HttpStatusCode.Forbidden, outsider.GetAsync($"{url}?pageSize=0"), "outsider with invalid paging hits authorization first");
        await AssertStatus(HttpStatusCode.NotFound, owner.GetAsync("/api/weddings/not-a-guid/audit-logs"), "malformed wedding id");

        var all = Enum.GetNames<WeddingPermission>();
        await AssertStatus(HttpStatusCode.Created, owner.PostAsJsonAsync(CoHostRoot(wedding.Id), new AddCoHostInput(cohostAccount.Email, all)), "add co-host with every permission");
        await AssertStatus(HttpStatusCode.Forbidden, cohost.GetAsync(url), "co-host with every permission");

        await AssertStatus(HttpStatusCode.NoContent, owner.DeleteAsync($"{CoHostRoot(wedding.Id)}/{cohostAccount.Id}"), "revoke");
        await AssertStatus(HttpStatusCode.Forbidden, cohost.GetAsync(url), "revoked co-host");
        await AssertStatus(HttpStatusCode.OK, owner.GetAsync(url), "owner");
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task CoHostLifecycleIsRecordedNewestFirstWithBeforeAndAfter() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "audit-life-owner");
        using var cohost = await CoHostPermissionTests.SignIn(factory, "audit-life-cohost");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "audit-life");
        var ownerAccount = await Me(owner);
        var cohostAccount = await Me(cohost);
        var root = CoHostRoot(wedding.Id);

        await AssertStatus(HttpStatusCode.Created, owner.PostAsJsonAsync(root, new AddCoHostInput(cohostAccount.Email, ["GUEST_VIEW"])), "add");
        await AssertStatus(HttpStatusCode.OK, owner.PutAsJsonAsync($"{root}/{cohostAccount.Id}/permissions", new UpdateCoHostPermissionsInput(["GUEST_VIEW", "GUEST_EDIT"])), "update");
        await AssertStatus(HttpStatusCode.NoContent, owner.DeleteAsync($"{root}/{cohostAccount.Id}"), "revoke");
        await AssertStatus(HttpStatusCode.Created, owner.PostAsJsonAsync(root, new AddCoHostInput(cohostAccount.Email, ["GIFT_VIEW"])), "reactivate");

        var page = await Query(owner, wedding.Id, "entityType=wedding_members");
        Assert.Equal(4, page.TotalCount);
        Assert.Equal(["COHOST_REACTIVATED", "COHOST_REVOKED", "COHOST_PERMISSIONS_UPDATED", "COHOST_ADDED"], page.Items.Select(x => x.Action).ToArray());
        Assert.Equal(page.Items.OrderByDescending(x => x.CreatedAt).Select(x => x.Id), page.Items.Select(x => x.Id));
        Assert.All(page.Items, item =>
        {
            Assert.Equal("USER", item.ActorType);
            Assert.Equal(ownerAccount.Id, item.ActorUserId);
            Assert.Equal(ownerAccount.Email, item.ActorEmail);
            Assert.Equal("wedding_members", item.EntityType);
            Assert.NotNull(item.EntityId);
            Assert.NotNull(item.Metadata);
            Assert.Equal(cohostAccount.Id, item.Metadata!.Value.GetProperty("targetUserId").GetGuid());
        });

        var reactivated = page.Items[0].Metadata!.Value;
        Assert.Equal("REVOKED", Status(reactivated, "before"));
        Assert.Empty(Permissions(reactivated, "before"));
        Assert.Equal("ACTIVE", Status(reactivated, "after"));
        Assert.Equal(["GIFT_VIEW"], Permissions(reactivated, "after"));

        var revoked = page.Items[1].Metadata!.Value;
        Assert.Equal("ACTIVE", Status(revoked, "before"));
        Assert.Equal(["GUEST_EDIT", "GUEST_VIEW"], Permissions(revoked, "before"));
        Assert.Equal("REVOKED", Status(revoked, "after"));
        Assert.Empty(Permissions(revoked, "after"));

        var updated = page.Items[2].Metadata!.Value;
        Assert.Equal(["GUEST_VIEW"], Permissions(updated, "before"));
        Assert.Equal(["GUEST_EDIT", "GUEST_VIEW"], Permissions(updated, "after"));
        Assert.Equal("ACTIVE", Status(updated, "before"));
        Assert.Equal("ACTIVE", Status(updated, "after"));

        var added = page.Items[3].Metadata!.Value;
        Assert.Equal(JsonValueKind.Null, added.GetProperty("before").ValueKind);
        Assert.Equal("ACTIVE", Status(added, "after"));
        Assert.Equal(["GUEST_VIEW"], Permissions(added, "after"));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task RejectedAndIdempotentCoHostCallsDoNotPolluteTheHistory() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "audit-noise-owner");
        using var cohost = await CoHostPermissionTests.SignIn(factory, "audit-noise-cohost");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "audit-noise");
        var cohostAccount = await Me(cohost);
        var root = CoHostRoot(wedding.Id);

        await AssertStatus(HttpStatusCode.Created, owner.PostAsJsonAsync(root, new AddCoHostInput(cohostAccount.Email, ["GUEST_VIEW"])), "add");
        var before = (await Query(owner, wedding.Id)).TotalCount;

        await AssertStatus(HttpStatusCode.Conflict, owner.PostAsJsonAsync(root, new AddCoHostInput(cohostAccount.Email, ["GUEST_VIEW"])), "duplicate add");
        await AssertStatus(HttpStatusCode.BadRequest, owner.PostAsJsonAsync(root, new AddCoHostInput(cohostAccount.Email, ["1"])), "numeric permission");
        await AssertStatus(HttpStatusCode.Forbidden, cohost.PutAsJsonAsync($"{root}/{cohostAccount.Id}/permissions", new UpdateCoHostPermissionsInput(["GUEST_EDIT"])), "co-host update");
        await AssertStatus(HttpStatusCode.NotFound, owner.DeleteAsync($"{root}/{Guid.NewGuid()}"), "unknown revoke");
        Assert.Equal(before, (await Query(owner, wedding.Id)).TotalCount);

        await AssertStatus(HttpStatusCode.NoContent, owner.DeleteAsync($"{root}/{cohostAccount.Id}"), "revoke");
        await AssertStatus(HttpStatusCode.NoContent, owner.DeleteAsync($"{root}/{cohostAccount.Id}"), "repeat revoke");
        Assert.Equal(before + 1, (await Query(owner, wedding.Id)).TotalCount);
        Assert.Equal(1, (await Query(owner, wedding.Id, "action=COHOST_REVOKED")).TotalCount);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task FiltersPagingAndValidationBehaveAsDocumented() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "audit-filter-owner");
        using var cohost = await CoHostPermissionTests.SignIn(factory, "audit-filter-cohost");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "audit-filter");
        var cohostAccount = await Me(cohost);
        var root = CoHostRoot(wedding.Id);

        await AssertStatus(HttpStatusCode.Created, owner.PostAsJsonAsync(root, new AddCoHostInput(cohostAccount.Email, ["GUEST_VIEW"])), "add");
        await AssertStatus(HttpStatusCode.OK, owner.PutAsJsonAsync($"{root}/{cohostAccount.Id}/permissions", new UpdateCoHostPermissionsInput(["GUEST_EDIT"])), "update");
        await AssertStatus(HttpStatusCode.NoContent, owner.DeleteAsync($"{root}/{cohostAccount.Id}"), "revoke");

        var expectedTotal = await db.AuditLogs.CountAsync(x => x.WeddingId == wedding.Id);
        var everything = await Query(owner, wedding.Id);
        Assert.Equal(expectedTotal, everything.TotalCount);
        Assert.Equal(expectedTotal, everything.Items.Count);
        Assert.Equal(1, everything.Page);
        Assert.Equal(25, everything.PageSize);

        var memberId = everything.Items.First(x => x.Action == "COHOST_ADDED").EntityId!.Value;
        Assert.Equal(3, (await Query(owner, wedding.Id, $"entityId={memberId}")).TotalCount);
        Assert.Equal(3, (await Query(owner, wedding.Id, "entityType=wedding_members")).TotalCount);
        Assert.Equal(1, (await Query(owner, wedding.Id, "action=COHOST_PERMISSIONS_UPDATED")).TotalCount);
        Assert.Equal(0, (await Query(owner, wedding.Id, "action=cohost_added")).TotalCount);
        Assert.Equal(0, (await Query(owner, wedding.Id, "action=COHOST_ADDED&entityType=weddings")).TotalCount);
        Assert.Equal(0, (await Query(owner, wedding.Id, $"entityId={Guid.NewGuid()}")).TotalCount);

        var pageOne = await Query(owner, wedding.Id, "pageSize=2&page=1");
        var pageTwo = await Query(owner, wedding.Id, "pageSize=2&page=2");
        var pageThree = await Query(owner, wedding.Id, "pageSize=2&page=3");
        Assert.Equal(expectedTotal, pageOne.TotalCount);
        Assert.Equal(2, pageOne.Items.Count);
        Assert.Single(pageTwo.Items);
        Assert.Empty(pageThree.Items);
        Assert.Equal(expectedTotal, pageOne.Items.Concat(pageTwo.Items).Select(x => x.Id).Distinct().Count());
        Assert.Equal(everything.Items.Take(4).Select(x => x.Id), pageOne.Items.Concat(pageTwo.Items).Select(x => x.Id));

        var future = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"));
        var past = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));
        Assert.Equal(0, (await Query(owner, wedding.Id, $"from={future}")).TotalCount);
        Assert.Equal(0, (await Query(owner, wedding.Id, $"to={past}")).TotalCount);
        Assert.Equal(expectedTotal, (await Query(owner, wedding.Id, $"from={past}&to={future}")).TotalCount);

        var url = Url(wedding.Id);
        foreach (var bad in new[] { "pageSize=0", "pageSize=101", "pageSize=-1", "page=0", "page=-1", $"from={future}&to={past}", $"action={new string('A', 101)}", $"entityType={new string('A', 101)}", $"entityId={Guid.Empty}" })
            await AssertStatus(HttpStatusCode.BadRequest, owner.GetAsync($"{url}?{bad}"), $"invalid query '{bad}'");
        await AssertStatus(HttpStatusCode.BadRequest, owner.GetAsync($"{url}?entityId=not-a-guid"), "malformed entityId");
        await AssertStatus(HttpStatusCode.BadRequest, owner.GetAsync($"{url}?from=yesterday"), "malformed from");
        await AssertStatus(HttpStatusCode.OK, owner.GetAsync($"{url}?pageSize=100"), "max page size");
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task HistoryIsScopedToItsOwnWedding() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var ownerA = await CoHostPermissionTests.SignIn(factory, "audit-scope-a");
        using var ownerB = await CoHostPermissionTests.SignIn(factory, "audit-scope-b");
        using var cohost = await CoHostPermissionTests.SignIn(factory, "audit-scope-cohost");
        var weddingA = await CoHostPermissionTests.CreateWedding(db, ownerA, "audit-scope-wa");
        var weddingB = await CoHostPermissionTests.CreateWedding(db, ownerB, "audit-scope-wb");
        var cohostAccount = await Me(cohost);

        await AssertStatus(HttpStatusCode.Created, ownerA.PostAsJsonAsync(CoHostRoot(weddingA.Id), new AddCoHostInput(cohostAccount.Email, ["GUEST_VIEW"])), "add on A");

        var pageA = await Query(ownerA, weddingA.Id);
        var pageB = await Query(ownerB, weddingB.Id);
        Assert.Contains(pageA.Items, x => x.Action == "COHOST_ADDED");
        Assert.DoesNotContain(pageB.Items, x => x.Action == "COHOST_ADDED");
        Assert.Empty(pageA.Items.Select(x => x.Id).Intersect(pageB.Items.Select(x => x.Id)));

        await AssertStatus(HttpStatusCode.Forbidden, ownerB.GetAsync(Url(weddingA.Id)), "owner B reading wedding A");
        await AssertStatus(HttpStatusCode.Forbidden, ownerA.GetAsync(Url(weddingB.Id)), "owner A reading wedding B");
        var foreignEntity = pageA.Items.First(x => x.Action == "COHOST_ADDED").EntityId!.Value;
        Assert.Equal(0, (await Query(ownerB, weddingB.Id, $"entityId={foreignEntity}")).TotalCount);
    });


    private static string Url(Guid weddingId) => $"/api/weddings/{weddingId}/audit-logs";

    private static string CoHostRoot(Guid weddingId) => $"/api/weddings/{weddingId}/co-hosts";

    private static async Task<AccountDto> Me(HttpClient client) => (await client.GetFromJsonAsync<AccountDto>("/api/auth/me"))!;

    private static async Task<PagedResult<AuditLogDto>> Query(HttpClient client, Guid weddingId, string? query = null)
    {
        var response = await client.GetAsync(query is null ? Url(weddingId) : $"{Url(weddingId)}?{query}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"audit query '{query}': expected 200 but got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<PagedResult<AuditLogDto>>())!;
    }

    private static string Status(JsonElement metadata, string side) => metadata.GetProperty(side).GetProperty("status").GetString()!;

    private static string[] Permissions(JsonElement metadata, string side) =>
        metadata.GetProperty(side).GetProperty("permissions").EnumerateArray().Select(x => x.GetString()!).ToArray();

    private static async Task AssertStatus(HttpStatusCode expected, Task<HttpResponseMessage> call, string label)
    {
        var response = await call;
        Assert.True(expected == response.StatusCode, $"{label}: expected {(int)expected} but got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }
}
