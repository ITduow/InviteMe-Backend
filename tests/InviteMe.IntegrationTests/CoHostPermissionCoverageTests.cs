using System.Net;
using System.Net.Http.Json;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Weddings.CoHosts;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.IntegrationTests;

public sealed class CoHostPermissionCoverageTests
{
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task AnonymousRequestsToEveryCoHostEndpointAreRejectedWith401() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "anon-owner");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "coh-anon");
        using var anonymous = factory.CreateHttpsClient();
        var root = Root(wedding.Id);
        var someUser = Guid.NewGuid();

        await AssertStatus(HttpStatusCode.Unauthorized, anonymous.GetAsync(root), "GET list");
        await AssertStatus(HttpStatusCode.Unauthorized, anonymous.GetAsync($"{root}/{someUser}"), "GET by id");
        await AssertStatus(HttpStatusCode.Unauthorized, anonymous.PostAsJsonAsync(InvitationsRoot(wedding.Id),
            new CreateCoHostInvitationInput("x@example.test", [new("GUEST_VIEW", [], true)])), "POST invitation");
        await AssertStatus(HttpStatusCode.Unauthorized, SetPermissions(anonymous, wedding.Id, someUser, "GUEST_VIEW"), "PUT");
        await AssertStatus(HttpStatusCode.Unauthorized, anonymous.DeleteAsync($"{root}/{someUser}"), "DELETE");
        Assert.Equal(0, await db.AuditLogs.CountAsync(x => x.EntityType == "wedding_members"));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task MissingWeddingAndOutsidersAreDeniedWithoutLeakingExistence() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "leak-owner");
        using var outsider = await CoHostPermissionTests.SignIn(factory, "leak-outsider");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "coh-leak");
        var missingId = Guid.NewGuid();
        var missing = Root(missingId);
        var someUser = Guid.NewGuid();

        foreach (var (name, client) in new[] { ("owner", owner), ("outsider", outsider) })
        {
            await AssertStatus(HttpStatusCode.Forbidden, client.GetAsync(missing), $"{name} GET list on missing wedding");
            await AssertStatus(HttpStatusCode.Forbidden, client.GetAsync($"{missing}/{someUser}"), $"{name} GET by id on missing wedding");
            await AssertStatus(HttpStatusCode.Forbidden, SetPermissions(client, missingId, someUser, "GUEST_VIEW"), $"{name} PUT on missing wedding");
            await AssertStatus(HttpStatusCode.Forbidden, client.DeleteAsync($"{missing}/{someUser}"), $"{name} DELETE on missing wedding");
        }

        var real = Root(wedding.Id);
        await AssertStatus(HttpStatusCode.Forbidden, outsider.PostAsJsonAsync(InvitationsRoot(wedding.Id),
            new CreateCoHostInvitationInput("not-an-email", [new("BOGUS", [], true)])), "outsider invalid POST must hit authorization first");
        await AssertStatus(HttpStatusCode.Forbidden, SetPermissions(outsider, wedding.Id, someUser, "BOGUS"), "outsider invalid PUT must hit authorization first");
        await AssertStatus(HttpStatusCode.NotFound, owner.GetAsync("/api/weddings/not-a-guid/co-hosts"), "malformed wedding id");
        await AssertStatus(HttpStatusCode.NotFound, owner.GetAsync($"{real}/not-a-guid"), "malformed co-host id");
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task CoHostWithEveryPermissionStillCannotManageCoHosts() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "full-owner");
        using var cohost = await CoHostPermissionTests.SignIn(factory, "full-cohost");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "coh-full");
        var cohostAccount = await Me(cohost);
        var ownerAccount = await Me(owner);
        var allPermissions = CoHostPermissionTests.Delegable;
        Assert.Equal(6, allPermissions.Length);

        var added = await CoHostPermissionTests.AddCoHostAsync(owner, cohost, wedding.Id, allPermissions);
        Assert.Equal(6, added.Permissions.Count);
        var root = Root(wedding.Id);

        await AssertStatus(HttpStatusCode.Forbidden, cohost.GetAsync(root), "GET list");
        await AssertStatus(HttpStatusCode.Forbidden, cohost.GetAsync($"{root}/{cohostAccount.Id}"), "GET by id");
        await AssertStatus(HttpStatusCode.Forbidden, cohost.PostAsJsonAsync(InvitationsRoot(wedding.Id),
            new CreateCoHostInvitationInput(ownerAccount.Email, [new("GUEST_VIEW", [], true)])), "POST invitation");
        await AssertStatus(HttpStatusCode.Forbidden, SetPermissions(cohost, wedding.Id, cohostAccount.Id, allPermissions), "PUT self");
        await AssertStatus(HttpStatusCode.Forbidden, cohost.DeleteAsync($"{root}/{cohostAccount.Id}"), "DELETE self");

        var stored = (await owner.GetFromJsonAsync<CoHostDto>($"{root}/{cohostAccount.Id}"))!;
        Assert.Equal("ACTIVE", stored.Status);
        Assert.Equal(6, stored.Permissions.Count);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task EveryMutationWritesOneAuditRowAndRejectedRequestsWriteNone() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "audit-owner");
        using var cohost = await CoHostPermissionTests.SignIn(factory, "audit-cohost");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "coh-audit");
        var ownerAccount = await Me(owner);
        var cohostAccount = await Me(cohost);

        var added = await CoHostPermissionTests.AddCoHostAsync(owner, cohost, wedding.Id, "GUEST_VIEW");
        await AssertStatus(HttpStatusCode.OK, SetPermissions(owner, wedding.Id, cohostAccount.Id, "GUEST_VIEW", "GUEST_EDIT"), "update");
        await AssertStatus(HttpStatusCode.NoContent, owner.DeleteAsync($"{Root(wedding.Id)}/{cohostAccount.Id}"), "revoke");
        await CoHostPermissionTests.AddCoHostAsync(owner, cohost, wedding.Id, "RSVP_VIEW");

        var rows = await db.AuditLogs.AsNoTracking()
            .Where(x => x.WeddingId == wedding.Id && x.EntityType == "wedding_members")
            .OrderBy(x => x.CreatedAt).ToListAsync();
        Assert.Equal(["COHOST_INVITATION_ACCEPTED", "COHOST_SCOPES_UPDATED", "COHOST_REVOKED", "COHOST_INVITATION_ACCEPTED"], rows.Select(x => x.Action).ToArray());
        Assert.All(rows, row =>
        {
            Assert.Equal("USER", row.ActorType);
            Assert.Equal(added.Id, row.EntityId);
        });

        var root = Root(wedding.Id);
        var invitationsUrl = InvitationsRoot(wedding.Id);
        var before = await db.AuditLogs.CountAsync();
        await AssertStatus(HttpStatusCode.Forbidden, SetPermissions(cohost, wedding.Id, cohostAccount.Id, "GUEST_EDIT"), "co-host PUT");
        await AssertStatus(HttpStatusCode.Forbidden, cohost.DeleteAsync($"{root}/{cohostAccount.Id}"), "co-host DELETE");
        await AssertStatus(HttpStatusCode.BadRequest, owner.PostAsJsonAsync(invitationsUrl,
            new CreateCoHostInvitationInput("bad", [new("GUEST_VIEW", [], true)])), "invalid email");
        await AssertStatus(HttpStatusCode.NotFound, SetPermissions(owner, wedding.Id, Guid.NewGuid(), "GUEST_VIEW"), "unknown member");
        await AssertStatus(HttpStatusCode.Conflict, owner.PostAsJsonAsync(invitationsUrl,
            new CreateCoHostInvitationInput(cohostAccount.Email, [new("GUEST_VIEW", [], true)])), "already active");
        await AssertStatus(HttpStatusCode.Conflict, owner.PostAsJsonAsync(invitationsUrl,
            new CreateCoHostInvitationInput(ownerAccount.Email, [new("GUEST_VIEW", [], true)])), "owner as co-host");
        Assert.Equal(before, await db.AuditLogs.CountAsync());
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task OnlyExactPermissionCodesAreAcceptedAndNothingIsPersistedOnRejection() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "codes-owner");
        using var cohost = await CoHostPermissionTests.SignIn(factory, "codes-cohost");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "coh-codes");
        var cohostAccount = await Me(cohost);
        var root = Root(wedding.Id);
        var invitationsUrl = InvitationsRoot(wedding.Id);
        string[] invalidCodes =
        [
            "1", "0", "999", "-1", "guest_view", "Guest_View", " GUEST_VIEW", "GUEST_VIEW ",
            "GUEST_VIEW,GUEST_EDIT", "GUEST_VIEW | GUEST_EDIT", "NOT_A_PERMISSION", "OWNER_ONLY", ""
        ];

        foreach (var code in invalidCodes)
            await AssertStatus(HttpStatusCode.BadRequest, owner.PostAsJsonAsync(invitationsUrl,
                new CreateCoHostInvitationInput(cohostAccount.Email, [new(code, [], true)])), $"POST code '{code}'");
        await AssertStatus(HttpStatusCode.BadRequest, owner.PostAsJsonAsync(invitationsUrl,
            new CreateCoHostInvitationInput(cohostAccount.Email, [new("GUEST_VIEW", [], true), new("1", [], true)])), "valid mixed with numeric");
        await AssertStatus(HttpStatusCode.BadRequest, owner.PostAsJsonAsync(invitationsUrl,
            new CreateCoHostInvitationInput(cohostAccount.Email, [new("GUEST_VIEW", [], true), new("GUEST_VIEW", [], true)])), "duplicate");
        await AssertStatus(HttpStatusCode.BadRequest, owner.PostAsJsonAsync(invitationsUrl,
            new CreateCoHostInvitationInput(cohostAccount.Email, [new("GUEST_VIEW", [], true), new("guest_view", [], true)])), "case duplicate");
        Assert.Equal(0, await db.WeddingMembers.CountAsync(x => x.WeddingId == wedding.Id));
        Assert.Equal(0, await db.WeddingMemberPermissions.CountAsync());

        await CoHostPermissionTests.AddCoHostAsync(owner, cohost, wedding.Id, "GUEST_VIEW");
        foreach (var code in invalidCodes)
            await AssertStatus(HttpStatusCode.BadRequest, SetPermissions(owner, wedding.Id, cohostAccount.Id, code), $"PUT code '{code}'");
        await AssertStatus(HttpStatusCode.BadRequest, SetPermissions(owner, wedding.Id, cohostAccount.Id, "GUEST_EDIT", "2"), "PUT valid mixed with numeric");

        var stored = (await owner.GetFromJsonAsync<CoHostDto>($"{root}/{cohostAccount.Id}"))!;
        Assert.Equal(["GUEST_VIEW"], stored.Permissions.ToArray());
        Assert.Equal(1, await db.WeddingMemberPermissions.CountAsync());
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task RevokedMembersStayVisibleToOwnerCannotBeEditedAndUnknownTargetsReturn404() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "state-owner");
        using var cohost = await CoHostPermissionTests.SignIn(factory, "state-cohost");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "coh-state");
        var ownerAccount = await Me(owner);
        var cohostAccount = await Me(cohost);
        var root = Root(wedding.Id);

        await CoHostPermissionTests.AddCoHostAsync(owner, cohost, wedding.Id, "GUEST_VIEW", "RSVP_VIEW");
        await AssertStatus(HttpStatusCode.NoContent, owner.DeleteAsync($"{root}/{cohostAccount.Id}"), "revoke");

        var detail = (await owner.GetFromJsonAsync<CoHostDto>($"{root}/{cohostAccount.Id}"))!;
        Assert.Equal("REVOKED", detail.Status);
        Assert.Empty(detail.Permissions);
        var list = (await owner.GetFromJsonAsync<List<CoHostDto>>(root))!;
        var listed = Assert.Single(list);
        Assert.Equal("REVOKED", listed.Status);
        Assert.Empty(listed.Permissions);
        Assert.Equal(0, await db.WeddingMemberPermissions.CountAsync());

        await AssertStatus(HttpStatusCode.Conflict, SetPermissions(owner, wedding.Id, cohostAccount.Id, "GUEST_VIEW"), "update revoked member");

        var unknown = Guid.NewGuid();
        await AssertStatus(HttpStatusCode.NotFound, owner.GetAsync($"{root}/{unknown}"), "GET unknown");
        await AssertStatus(HttpStatusCode.NotFound, SetPermissions(owner, wedding.Id, unknown, "GUEST_VIEW"), "PUT unknown");
        await AssertStatus(HttpStatusCode.NotFound, owner.DeleteAsync($"{root}/{unknown}"), "DELETE unknown");
        await AssertStatus(HttpStatusCode.NotFound, owner.GetAsync($"{root}/{ownerAccount.Id}"), "GET owner as member");
        await AssertStatus(HttpStatusCode.NotFound, SetPermissions(owner, wedding.Id, ownerAccount.Id, "GUEST_VIEW"), "PUT owner as member");
        await AssertStatus(HttpStatusCode.NotFound, owner.DeleteAsync($"{root}/{ownerAccount.Id}"), "DELETE owner as member");

        var again = await CoHostPermissionTests.AddCoHostAsync(owner, cohost, wedding.Id);
        Assert.Equal("ACTIVE", again.Status);
        Assert.Empty(again.Permissions);
        Assert.Equal(detail.Id, again.Id);
        Assert.Single(await db.WeddingMembers.AsNoTracking().Where(x => x.WeddingId == wedding.Id).ToListAsync());
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task RepeatedRevokeIsIdempotentAndWritesASingleAuditRow() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "idem-owner");
        using var cohost = await CoHostPermissionTests.SignIn(factory, "idem-cohost");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "coh-idem");
        var cohostAccount = await Me(cohost);
        var root = Root(wedding.Id);

        await CoHostPermissionTests.AddCoHostAsync(owner, cohost, wedding.Id, "GUEST_VIEW");
        await AssertStatus(HttpStatusCode.NoContent, owner.DeleteAsync($"{root}/{cohostAccount.Id}"), "first revoke");
        var stamp = (await db.WeddingMembers.AsNoTracking().SingleAsync(x => x.WeddingId == wedding.Id)).UpdatedAt;

        await AssertStatus(HttpStatusCode.NoContent, owner.DeleteAsync($"{root}/{cohostAccount.Id}"), "second revoke");
        await AssertStatus(HttpStatusCode.NoContent, owner.DeleteAsync($"{root}/{cohostAccount.Id}"), "third revoke");

        Assert.Equal(1, await db.AuditLogs.CountAsync(x => x.WeddingId == wedding.Id && x.Action == "COHOST_REVOKED"));
        Assert.Equal(stamp, (await db.WeddingMembers.AsNoTracking().SingleAsync(x => x.WeddingId == wedding.Id)).UpdatedAt);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task UpdatingPermissionsKeepsGrantTimestampOfUnchangedPermissions() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "diff-owner");
        using var cohost = await CoHostPermissionTests.SignIn(factory, "diff-cohost");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "coh-diff");
        var cohostAccount = await Me(cohost);
        var codes = await db.Permissions.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Code);

        await CoHostPermissionTests.AddCoHostAsync(owner, cohost, wedding.Id, "GUEST_VIEW", "RSVP_VIEW");
        var before = (await db.WeddingMemberPermissions.AsNoTracking().ToListAsync()).ToDictionary(x => codes[x.PermissionId], x => x.GrantedAt);
        await Task.Delay(50);

        var response = await SetPermissions(owner, wedding.Id, cohostAccount.Id, "GUEST_VIEW", "CHECKIN_MANAGE");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var after = (await db.WeddingMemberPermissions.AsNoTracking().ToListAsync()).ToDictionary(x => codes[x.PermissionId], x => x.GrantedAt);
        Assert.Equal(["CHECKIN_MANAGE", "GUEST_VIEW"], after.Keys.Order().ToArray());
        Assert.Equal(before["GUEST_VIEW"], after["GUEST_VIEW"]);
        Assert.True(after["CHECKIN_MANAGE"] > before["GUEST_VIEW"]);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task PermissionMissingFromTheCatalogIsRejectedAndNothingIsPersisted() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "seed-owner");
        using var cohost = await CoHostPermissionTests.SignIn(factory, "seed-cohost");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "coh-seed");
        var cohostAccount = await Me(cohost);
        var root = Root(wedding.Id);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM inviteme.permissions WHERE code = 'CHECKIN_MANAGE'");

        await AssertStatus(HttpStatusCode.UnprocessableEntity, owner.PostAsJsonAsync(InvitationsRoot(wedding.Id),
            new CreateCoHostInvitationInput(cohostAccount.Email, [new("GUEST_VIEW", [], true), new("CHECKIN_MANAGE", [], true)])), "add with missing catalog permission");
        Assert.Equal(0, await db.WeddingMembers.CountAsync(x => x.WeddingId == wedding.Id));
        Assert.Equal(0, await db.WeddingMemberPermissions.CountAsync());
        Assert.Equal(0, await db.AuditLogs.CountAsync(x => x.EntityType == "wedding_members"));

        await CoHostPermissionTests.AddCoHostAsync(owner, cohost, wedding.Id, "GUEST_VIEW");
        await AssertStatus(HttpStatusCode.UnprocessableEntity, SetPermissions(owner, wedding.Id, cohostAccount.Id, "CHECKIN_MANAGE"), "update with missing catalog permission");
        var stored = (await owner.GetFromJsonAsync<CoHostDto>($"{root}/{cohostAccount.Id}"))!;
        Assert.Equal(["GUEST_VIEW"], stored.Permissions.ToArray());
        Assert.Equal(1, await db.AuditLogs.CountAsync(x => x.EntityType == "wedding_members"));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ConcurrentUpdatesAndRevokeNeverLeaveGrantsOnARevokedMember() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "lock-owner");
        using var cohost = await CoHostPermissionTests.SignIn(factory, "lock-cohost");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "coh-lock");
        var cohostAccount = await Me(cohost);
        var root = Root(wedding.Id);
        await CoHostPermissionTests.AddCoHostAsync(owner, cohost, wedding.Id, "GUEST_VIEW");

        var calls = new List<Task<HttpResponseMessage>>
        {
            owner.DeleteAsync($"{root}/{cohostAccount.Id}"),
            SetPermissions(owner, wedding.Id, cohostAccount.Id, "GUEST_VIEW", "GUEST_EDIT"),
            SetPermissions(owner, wedding.Id, cohostAccount.Id, "RSVP_VIEW"),
            SetPermissions(owner, wedding.Id, cohostAccount.Id, "SEATING_VIEW", "SEATING_EDIT")
        };
        var responses = await Task.WhenAll(calls);

        Assert.Equal(HttpStatusCode.NoContent, responses[0].StatusCode);
        Assert.All(responses.Skip(1), r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, $"unexpected {(int)r.StatusCode}"));
        Assert.Equal("REVOKED", (await db.WeddingMembers.AsNoTracking().SingleAsync(x => x.WeddingId == wedding.Id)).Status);
        Assert.Equal(0, await db.WeddingMemberPermissions.CountAsync());
    });

    private static string Root(Guid weddingId) => $"/api/weddings/{weddingId}/co-hosts";

    private static string InvitationsRoot(Guid weddingId) => $"/api/weddings/{weddingId}/co-host-invitations";

    private static async Task<AccountDto> Me(HttpClient client) => (await client.GetFromJsonAsync<AccountDto>("/api/auth/me"))!;

    private static Task<HttpResponseMessage> SetPermissions(HttpClient client, Guid weddingId, Guid coHostUserId, params string[] permissions) =>
        client.PutAsJsonAsync($"{Root(weddingId)}/{coHostUserId}/scopes",
            new UpdateCoHostScopesInput(permissions.Select(p => new CoHostPermissionScopeInput(p, [], true)).ToArray()));

    private static async Task AssertStatus(HttpStatusCode expected, Task<HttpResponseMessage> call, string label)
    {
        var response = await call;
        Assert.True(expected == response.StatusCode, $"{label}: expected {(int)expected} but got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }
}
