using System.Net;
using System.Net.Http.Json;
using InviteMe.Application.Features.Gifts.Workflow;
using InviteMe.Application.Features.Guests.Import;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Invitations.Lifecycle;
using InviteMe.Application.Features.Reception;
using InviteMe.Application.Features.Seating.Workflow;
using InviteMe.Application.Features.Weddings.CoHosts;
using InviteMe.Application.Features.Weddings.Workspace;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.IntegrationTests;

public sealed class CoHostPermissionCoverageTests
{
    private static readonly (string Permission, string Path)[] ReadEndpoints =
    [
        ("WEDDING_VIEW", ""),
        ("GUEST_VIEW", "/guests"),
        ("RSVP_VIEW", "/waitlist"),
        ("SEATING_VIEW", "/tables"),
        ("CHECKIN_MANAGE", "/check-ins"),
        ("GIFT_VIEW", "/gifts"),
        ("ANALYTICS_VIEW", "/reports/summary")
    ];

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task AnonymousRequestsToEveryCoHostEndpointAreRejectedWith401() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "anon-owner");
        var wedding = await CoHostPermissionTests.CreateWedding(owner, "coh-anon");
        using var anonymous = factory.CreateHttpsClient();
        var root = Root(wedding.Id);
        var someUser = Guid.NewGuid();

        await AssertStatus(HttpStatusCode.Unauthorized, anonymous.GetAsync(root), "GET list");
        await AssertStatus(HttpStatusCode.Unauthorized, anonymous.GetAsync($"{root}/{someUser}"), "GET by id");
        await AssertStatus(HttpStatusCode.Unauthorized, anonymous.PostAsJsonAsync(root, new AddCoHostInput("x@example.test", ["GUEST_VIEW"])), "POST");
        await AssertStatus(HttpStatusCode.Unauthorized, anonymous.PutAsJsonAsync($"{root}/{someUser}/permissions", new UpdateCoHostPermissionsInput(["GUEST_VIEW"])), "PUT");
        await AssertStatus(HttpStatusCode.Unauthorized, anonymous.DeleteAsync($"{root}/{someUser}"), "DELETE");
        Assert.Equal(0, await db.AuditLogs.CountAsync(x => x.EntityType == "wedding_members"));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task MissingWeddingAndOutsidersAreDeniedWithoutLeakingExistence() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "leak-owner");
        using var outsider = await CoHostPermissionTests.SignIn(factory, "leak-outsider");
        var wedding = await CoHostPermissionTests.CreateWedding(owner, "coh-leak");
        var missing = Root(Guid.NewGuid());
        var someUser = Guid.NewGuid();

        foreach (var (name, client) in new[] { ("owner", owner), ("outsider", outsider) })
        {
            await AssertStatus(HttpStatusCode.Forbidden, client.GetAsync(missing), $"{name} GET list on missing wedding");
            await AssertStatus(HttpStatusCode.Forbidden, client.GetAsync($"{missing}/{someUser}"), $"{name} GET by id on missing wedding");
            await AssertStatus(HttpStatusCode.Forbidden, client.PostAsJsonAsync(missing, new AddCoHostInput("x@example.test", ["GUEST_VIEW"])), $"{name} POST on missing wedding");
            await AssertStatus(HttpStatusCode.Forbidden, client.PutAsJsonAsync($"{missing}/{someUser}/permissions", new UpdateCoHostPermissionsInput(["GUEST_VIEW"])), $"{name} PUT on missing wedding");
            await AssertStatus(HttpStatusCode.Forbidden, client.DeleteAsync($"{missing}/{someUser}"), $"{name} DELETE on missing wedding");
        }

        var real = Root(wedding.Id);
        await AssertStatus(HttpStatusCode.Forbidden, outsider.PostAsJsonAsync(real, new AddCoHostInput("not-an-email", ["BOGUS"])), "outsider invalid POST must hit authorization first");
        await AssertStatus(HttpStatusCode.Forbidden, outsider.PutAsJsonAsync($"{real}/{someUser}/permissions", new UpdateCoHostPermissionsInput(["BOGUS"])), "outsider invalid PUT must hit authorization first");
        await AssertStatus(HttpStatusCode.NotFound, owner.GetAsync("/api/weddings/not-a-guid/co-hosts"), "malformed wedding id");
        await AssertStatus(HttpStatusCode.NotFound, owner.GetAsync($"{real}/not-a-guid"), "malformed co-host id");
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task CoHostWithEveryPermissionStillCannotManageCoHosts() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "full-owner");
        using var cohost = await CoHostPermissionTests.SignIn(factory, "full-cohost");
        var wedding = await CoHostPermissionTests.CreateWedding(owner, "coh-full");
        var cohostAccount = await Me(cohost);
        var ownerAccount = await Me(owner);
        var allPermissions = Enum.GetNames<WeddingPermission>();
        Assert.Equal(12, allPermissions.Length);

        var added = await AddCoHost(owner, wedding.Id, cohostAccount.Email, allPermissions);
        Assert.Equal(12, added.Permissions.Count);
        var root = Root(wedding.Id);

        await AssertStatus(HttpStatusCode.Forbidden, cohost.GetAsync(root), "GET list");
        await AssertStatus(HttpStatusCode.Forbidden, cohost.GetAsync($"{root}/{cohostAccount.Id}"), "GET by id");
        await AssertStatus(HttpStatusCode.Forbidden, cohost.PostAsJsonAsync(root, new AddCoHostInput(ownerAccount.Email, ["GUEST_VIEW"])), "POST");
        await AssertStatus(HttpStatusCode.Forbidden, SetPermissions(cohost, wedding.Id, cohostAccount.Id, allPermissions), "PUT self");
        await AssertStatus(HttpStatusCode.Forbidden, cohost.DeleteAsync($"{root}/{cohostAccount.Id}"), "DELETE self");

        var stored = (await owner.GetFromJsonAsync<CoHostDto>($"{root}/{cohostAccount.Id}"))!;
        Assert.Equal("ACTIVE", stored.Status);
        Assert.Equal(12, stored.Permissions.Count);

        var ownerAccess = (await owner.GetFromJsonAsync<WeddingAccessDto>($"/api/weddings/{wedding.Id}/access"))!;
        Assert.Equal(12, ownerAccess.Permissions.Count);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task EachPermissionGrantsExactlyItsOwnReadEndpointAndSwitchesInstantly() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "matrix-owner");
        using var cohost = await CoHostPermissionTests.SignIn(factory, "matrix-cohost");
        var wedding = await CoHostPermissionTests.CreateWedding(owner, "coh-matrix");
        var cohostAccount = await Me(cohost);
        var weddingRoot = $"/api/weddings/{wedding.Id}";
        await AddCoHost(owner, wedding.Id, cohostAccount.Email);

        foreach (var granted in ReadEndpoints)
        {
            await AssertStatus(HttpStatusCode.OK, SetPermissions(owner, wedding.Id, cohostAccount.Id, granted.Permission), $"grant {granted.Permission}");
            foreach (var probe in ReadEndpoints)
            {
                var expected = probe.Permission == granted.Permission ? HttpStatusCode.OK : HttpStatusCode.Forbidden;
                await AssertStatus(expected, cohost.GetAsync(weddingRoot + probe.Path), $"granted={granted.Permission} probe={probe.Permission}");
            }
        }

        await AssertStatus(HttpStatusCode.OK, SetPermissions(owner, wedding.Id, cohostAccount.Id, "GIFT_VIEW"), "grant GIFT_VIEW only");
        await AssertStatus(HttpStatusCode.Forbidden, cohost.GetAsync($"{weddingRoot}/reports/summary?includeGifts=true"), "report with gifts needs analytics too");
        await AssertStatus(HttpStatusCode.OK, SetPermissions(owner, wedding.Id, cohostAccount.Id, "GIFT_VIEW", "ANALYTICS_VIEW"), "grant gift + analytics");
        await AssertStatus(HttpStatusCode.OK, cohost.GetAsync($"{weddingRoot}/reports/summary?includeGifts=true"), "report with gifts");
        await AssertStatus(HttpStatusCode.OK, SetPermissions(owner, wedding.Id, cohostAccount.Id, "ANALYTICS_VIEW"), "drop gift view");
        await AssertStatus(HttpStatusCode.Forbidden, cohost.GetAsync($"{weddingRoot}/reports/summary?includeGifts=true"), "gift view removed");
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task SameEndpointAllowsCoHostAAndDeniesCoHostB() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "ab-owner");
        using var coHostA = await CoHostPermissionTests.SignIn(factory, "ab-cohost-a");
        using var coHostB = await CoHostPermissionTests.SignIn(factory, "ab-cohost-b");
        var wedding = await CoHostPermissionTests.CreateWedding(owner, "coh-ab");
        await AddCoHost(owner, wedding.Id, (await Me(coHostA)).Email, "GIFT_VIEW", "GUEST_VIEW");
        await AddCoHost(owner, wedding.Id, (await Me(coHostB)).Email, "GUEST_VIEW");
        var gifts = $"/api/weddings/{wedding.Id}/gifts";
        var guests = $"/api/weddings/{wedding.Id}/guests";

        await AssertStatus(HttpStatusCode.OK, coHostA.GetAsync(gifts), "A on gifts");
        await AssertStatus(HttpStatusCode.Forbidden, coHostB.GetAsync(gifts), "B on gifts");
        await AssertStatus(HttpStatusCode.OK, coHostA.GetAsync(guests), "A on guests");
        await AssertStatus(HttpStatusCode.OK, coHostB.GetAsync(guests), "B on guests");

        var list = (await owner.GetFromJsonAsync<List<CoHostDto>>(Root(wedding.Id)))!;
        Assert.Equal(2, list.Count);
        Assert.Equal(["GIFT_VIEW", "GUEST_VIEW"], list.Single(x => x.Email == "ab-cohost-a@example.test").Permissions.ToArray());
        Assert.Equal(["GUEST_VIEW"], list.Single(x => x.Email == "ab-cohost-b@example.test").Permissions.ToArray());
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task WriteEndpointsStayForbiddenUntilTheMatchingPermissionIsGranted() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "write-owner");
        using var cohost = await CoHostPermissionTests.SignIn(factory, "write-cohost");
        var wedding = await CoHostPermissionTests.CreateWedding(owner, "coh-write");
        var cohostAccount = await Me(cohost);
        var w = $"/api/weddings/{wedding.Id}";
        string[] viewOnly = ["WEDDING_VIEW", "GUEST_VIEW", "RSVP_VIEW", "SEATING_VIEW", "GIFT_VIEW", "ANALYTICS_VIEW"];
        await AddCoHost(owner, wedding.Id, cohostAccount.Email, viewOnly);

        var writes = new (string Label, Func<Task<HttpResponseMessage>> Call)[]
        {
            ("POST guests", () => cohost.PostAsJsonAsync($"{w}/guests", new GuestInput("G1", "Guest One"))),
            ("POST guests/import", () => cohost.PostAsJsonAsync($"{w}/guests/import", new ImportGuests([new GuestInput("G1", "Guest One")]))),
            ("POST tables", () => cohost.PostAsJsonAsync($"{w}/tables", new CreateTable("T1", 4, "ACTIVE"))),
            ("POST gifts", () => cohost.PostAsJsonAsync($"{w}/gifts", new RecordGift(Guid.NewGuid(), null, 1, Guid.NewGuid()))),
            ("POST check-ins", () => cohost.PostAsJsonAsync($"{w}/check-ins", new CheckInInput(Guid.NewGuid()))),
            ("PUT settings", () => cohost.PutAsJsonAsync($"{w}/settings", new WorkspaceInput("T", "coh-write", 10, "Asia/Ho_Chi_Minh"))),
            ("POST invitations", () => cohost.PostAsJsonAsync($"{w}/invitations", new CreateInvitation(Guid.NewGuid(), new(Guid.NewGuid(), "a", "b", "c")))),
            ("POST invitations publish", () => cohost.PostAsJsonAsync($"{w}/invitations/{Guid.NewGuid()}/publish", new InvitationAction(1)))
        };
        foreach (var (label, call) in writes)
            await AssertStatus(HttpStatusCode.Forbidden, call(), $"view-only co-host: {label}");

        string[] withEdits = [.. viewOnly, "GUEST_EDIT", "SEATING_EDIT", "GIFT_EDIT", "CHECKIN_MANAGE", "WEDDING_EDIT", "INVITATION_SEND"];
        await AssertStatus(HttpStatusCode.OK, SetPermissions(owner, wedding.Id, cohostAccount.Id, withEdits), "grant edit permissions");

        var guestResponse = await cohost.PostAsJsonAsync($"{w}/guests", new GuestInput("G1", "Guest One"));
        Assert.Equal(HttpStatusCode.Created, guestResponse.StatusCode);
        var guestId = (await guestResponse.Content.ReadFromJsonAsync<ImportedGuestDto>())!.Id;
        Assert.True((await cohost.PostAsJsonAsync($"{w}/tables", new CreateTable("T1", 4, "ACTIVE"))).IsSuccessStatusCode);
        Assert.True((await cohost.PostAsJsonAsync($"{w}/gifts", new RecordGift(guestId, null, 100000, Guid.NewGuid()))).IsSuccessStatusCode);
        await AssertNotForbidden(cohost.PostAsJsonAsync($"{w}/check-ins", new CheckInInput(Guid.NewGuid())), "CHECKIN_MANAGE post");
        await AssertNotForbidden(cohost.PutAsJsonAsync($"{w}/settings", new WorkspaceInput("T", "coh-write", 10, "Asia/Ho_Chi_Minh")), "WEDDING_EDIT put");
        await AssertNotForbidden(cohost.PostAsJsonAsync($"{w}/invitations/{Guid.NewGuid()}/publish", new InvitationAction(1)), "INVITATION_SEND publish");
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task CoHostAccessIsIsolatedPerWeddingAndRevokeDoesNotLeakAcrossWeddings() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var ownerA = await CoHostPermissionTests.SignIn(factory, "iso-owner-a");
        using var ownerB = await CoHostPermissionTests.SignIn(factory, "iso-owner-b");
        using var cohost = await CoHostPermissionTests.SignIn(factory, "iso-cohost");
        var weddingA = await CoHostPermissionTests.CreateWedding(ownerA, "coh-iso-a");
        var weddingB = await CoHostPermissionTests.CreateWedding(ownerB, "coh-iso-b");
        var cohostAccount = await Me(cohost);
        await AddCoHost(ownerA, weddingA.Id, cohostAccount.Email, "GUEST_VIEW", "WEDDING_VIEW");
        var a = $"/api/weddings/{weddingA.Id}";
        var b = $"/api/weddings/{weddingB.Id}";

        await AssertStatus(HttpStatusCode.OK, cohost.GetAsync($"{a}/guests"), "own wedding guests");
        await AssertStatus(HttpStatusCode.Forbidden, cohost.GetAsync($"{b}/guests"), "other wedding guests");
        await AssertStatus(HttpStatusCode.Forbidden, cohost.GetAsync(b), "other wedding workspace");
        await AssertStatus(HttpStatusCode.Forbidden, cohost.GetAsync($"{b}/access"), "other wedding access");
        await AssertStatus(HttpStatusCode.Forbidden, cohost.GetAsync(Root(weddingB.Id)), "other wedding co-hosts");

        await AssertStatus(HttpStatusCode.Forbidden, ownerA.GetAsync(Root(weddingB.Id)), "owner A lists B co-hosts");
        await AssertStatus(HttpStatusCode.Forbidden, ownerA.PostAsJsonAsync(Root(weddingB.Id), new AddCoHostInput(cohostAccount.Email, ["GUEST_VIEW"])), "owner A adds to B");
        await AssertStatus(HttpStatusCode.Forbidden, SetPermissions(ownerA, weddingB.Id, cohostAccount.Id, "GUEST_EDIT"), "owner A updates B");
        await AssertStatus(HttpStatusCode.Forbidden, ownerA.DeleteAsync($"{Root(weddingB.Id)}/{cohostAccount.Id}"), "owner A revokes in B");
        await AssertStatus(HttpStatusCode.Forbidden, SetPermissions(ownerB, weddingA.Id, cohostAccount.Id, "GUEST_EDIT"), "owner B updates A");
        await AssertStatus(HttpStatusCode.Forbidden, ownerB.DeleteAsync($"{Root(weddingA.Id)}/{cohostAccount.Id}"), "owner B revokes in A");

        await AssertStatus(HttpStatusCode.NotFound, ownerB.GetAsync($"{Root(weddingB.Id)}/{cohostAccount.Id}"), "B GET non-member");
        await AssertStatus(HttpStatusCode.NotFound, SetPermissions(ownerB, weddingB.Id, cohostAccount.Id, "GUEST_VIEW"), "B PUT non-member");
        await AssertStatus(HttpStatusCode.NotFound, ownerB.DeleteAsync($"{Root(weddingB.Id)}/{cohostAccount.Id}"), "B DELETE non-member");

        await AddCoHost(ownerB, weddingB.Id, cohostAccount.Email, "GIFT_VIEW");
        await AssertStatus(HttpStatusCode.OK, cohost.GetAsync($"{b}/gifts"), "granted on B");
        await AssertStatus(HttpStatusCode.Forbidden, cohost.GetAsync($"{b}/guests"), "B grant is not A grant");

        await AssertStatus(HttpStatusCode.NoContent, ownerA.DeleteAsync($"{Root(weddingA.Id)}/{cohostAccount.Id}"), "revoke in A");
        await AssertStatus(HttpStatusCode.Forbidden, cohost.GetAsync($"{a}/guests"), "revoked in A");
        await AssertStatus(HttpStatusCode.OK, cohost.GetAsync($"{b}/gifts"), "B unaffected by A revoke");

        var memberships = await db.WeddingMembers.AsNoTracking().Where(x => x.UserId == cohostAccount.Id).ToListAsync();
        Assert.Equal("REVOKED", memberships.Single(x => x.WeddingId == weddingA.Id).Status);
        Assert.Equal("ACTIVE", memberships.Single(x => x.WeddingId == weddingB.Id).Status);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task EveryMutationWritesOneAuditRowAndRejectedRequestsWriteNone() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "audit-owner");
        using var cohost = await CoHostPermissionTests.SignIn(factory, "audit-cohost");
        var wedding = await CoHostPermissionTests.CreateWedding(owner, "coh-audit");
        var ownerAccount = await Me(owner);
        var cohostAccount = await Me(cohost);

        var added = await AddCoHost(owner, wedding.Id, cohostAccount.Email, "GUEST_VIEW");
        await AssertStatus(HttpStatusCode.OK, SetPermissions(owner, wedding.Id, cohostAccount.Id, "GUEST_VIEW", "GUEST_EDIT"), "update");
        await AssertStatus(HttpStatusCode.NoContent, owner.DeleteAsync($"{Root(wedding.Id)}/{cohostAccount.Id}"), "revoke");
        await AddCoHost(owner, wedding.Id, cohostAccount.Email, "GIFT_VIEW");

        var rows = await db.AuditLogs.AsNoTracking()
            .Where(x => x.WeddingId == wedding.Id && x.EntityType == "wedding_members")
            .OrderBy(x => x.CreatedAt).ToListAsync();
        Assert.Equal(["COHOST_ADDED", "COHOST_PERMISSIONS_UPDATED", "COHOST_REVOKED", "COHOST_REACTIVATED"], rows.Select(x => x.Action).ToArray());
        Assert.All(rows, row =>
        {
            Assert.Equal("USER", row.ActorType);
            Assert.Equal(ownerAccount.Id, row.ActorUserId);
            Assert.Equal(added.Id, row.EntityId);
        });

        var root = Root(wedding.Id);
        var before = await db.AuditLogs.CountAsync();
        await AssertStatus(HttpStatusCode.Forbidden, SetPermissions(cohost, wedding.Id, cohostAccount.Id, "GUEST_EDIT"), "co-host PUT");
        await AssertStatus(HttpStatusCode.Forbidden, cohost.DeleteAsync($"{root}/{cohostAccount.Id}"), "co-host DELETE");
        await AssertStatus(HttpStatusCode.BadRequest, owner.PostAsJsonAsync(root, new AddCoHostInput("bad", ["GUEST_VIEW"])), "invalid email");
        await AssertStatus(HttpStatusCode.NotFound, SetPermissions(owner, wedding.Id, Guid.NewGuid(), "GUEST_VIEW"), "unknown member");
        await AssertStatus(HttpStatusCode.Conflict, owner.PostAsJsonAsync(root, new AddCoHostInput(cohostAccount.Email, ["GUEST_VIEW"])), "already active");
        await AssertStatus(HttpStatusCode.NotFound, owner.PostAsJsonAsync(root, new AddCoHostInput("ghost@example.test", ["GUEST_VIEW"])), "unknown email");
        await AssertStatus(HttpStatusCode.Conflict, owner.PostAsJsonAsync(root, new AddCoHostInput(ownerAccount.Email, ["GUEST_VIEW"])), "owner as co-host");
        Assert.Equal(before, await db.AuditLogs.CountAsync());
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task OnlyExactPermissionCodesAreAcceptedAndNothingIsPersistedOnRejection() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "codes-owner");
        using var cohost = await CoHostPermissionTests.SignIn(factory, "codes-cohost");
        var wedding = await CoHostPermissionTests.CreateWedding(owner, "coh-codes");
        var cohostAccount = await Me(cohost);
        var root = Root(wedding.Id);
        string[] invalidCodes =
        [
            "1", "0", "999", "-1", "guest_view", "Guest_View", " GUEST_VIEW", "GUEST_VIEW ",
            "GUEST_VIEW,GUEST_EDIT", "GUEST_VIEW | GUEST_EDIT", "NOT_A_PERMISSION", "OWNER_ONLY", ""
        ];

        foreach (var code in invalidCodes)
            await AssertStatus(HttpStatusCode.BadRequest, owner.PostAsJsonAsync(root, new AddCoHostInput(cohostAccount.Email, [code])), $"POST code '{code}'");
        await AssertStatus(HttpStatusCode.BadRequest, owner.PostAsJsonAsync(root, new AddCoHostInput(cohostAccount.Email, ["GUEST_VIEW", "1"])), "valid mixed with numeric");
        await AssertStatus(HttpStatusCode.BadRequest, owner.PostAsJsonAsync(root, new AddCoHostInput(cohostAccount.Email, ["GUEST_VIEW", "GUEST_VIEW"])), "duplicate");
        await AssertStatus(HttpStatusCode.BadRequest, owner.PostAsJsonAsync(root, new AddCoHostInput(cohostAccount.Email, ["GUEST_VIEW", "guest_view"])), "case duplicate");
        Assert.Equal(0, await db.WeddingMembers.CountAsync(x => x.WeddingId == wedding.Id));
        Assert.Equal(0, await db.WeddingMemberPermissions.CountAsync());

        await AddCoHost(owner, wedding.Id, cohostAccount.Email, "GUEST_VIEW");
        foreach (var code in invalidCodes)
            await AssertStatus(HttpStatusCode.BadRequest, SetPermissions(owner, wedding.Id, cohostAccount.Id, code), $"PUT code '{code}'");
        await AssertStatus(HttpStatusCode.BadRequest, SetPermissions(owner, wedding.Id, cohostAccount.Id, "GUEST_EDIT", "2"), "PUT valid mixed with numeric");

        var stored = (await owner.GetFromJsonAsync<CoHostDto>($"{root}/{cohostAccount.Id}"))!;
        Assert.Equal(["GUEST_VIEW"], stored.Permissions.ToArray());
        Assert.Equal(1, await db.WeddingMemberPermissions.CountAsync());
        await AssertStatus(HttpStatusCode.OK, cohost.GetAsync($"/api/weddings/{wedding.Id}/guests"), "original grant intact");
        await AssertStatus(HttpStatusCode.Forbidden, cohost.PostAsJsonAsync($"/api/weddings/{wedding.Id}/guests", new GuestInput("G1", "Guest")), "no leaked edit grant");
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ZeroPermissionCoHostIsActiveButCanDoNothingAndInputEdgeCasesAreHandled() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "zero-owner");
        using var cohost = await CoHostPermissionTests.SignIn(factory, "zero-cohost");
        var wedding = await CoHostPermissionTests.CreateWedding(owner, "coh-zero");
        var cohostAccount = await Me(cohost);
        var root = Root(wedding.Id);
        var guests = $"/api/weddings/{wedding.Id}/guests";

        await AssertStatus(HttpStatusCode.BadRequest, owner.PostAsJsonAsync(root, new { email = cohostAccount.Email, permissions = (string[]?)null }), "null permissions");
        await AssertStatus(HttpStatusCode.BadRequest, owner.PostAsJsonAsync(root, new { email = cohostAccount.Email }), "missing permissions");
        await AssertStatus(HttpStatusCode.BadRequest, owner.PostAsJsonAsync(root, new { permissions = new[] { "GUEST_VIEW" } }), "missing email");
        await AssertStatus(HttpStatusCode.BadRequest, owner.PutAsJsonAsync($"{root}/{cohostAccount.Id}/permissions", new { permissions = (string[]?)null }), "PUT null permissions");

        var added = await AddCoHost(owner, wedding.Id, cohostAccount.Email.ToUpperInvariant());
        Assert.Equal("ACTIVE", added.Status);
        Assert.Empty(added.Permissions);
        Assert.Equal(cohostAccount.Email, added.Email);

        var access = (await cohost.GetFromJsonAsync<WeddingAccessDto>($"/api/weddings/{wedding.Id}/access"))!;
        Assert.Empty(access.Permissions);
        await AssertStatus(HttpStatusCode.Forbidden, cohost.GetAsync(guests), "zero-permission guests");

        await AssertStatus(HttpStatusCode.OK, SetPermissions(owner, wedding.Id, cohostAccount.Id, "GUEST_VIEW"), "grant");
        await AssertStatus(HttpStatusCode.OK, cohost.GetAsync(guests), "granted guests");
        var cleared = await SetPermissions(owner, wedding.Id, cohostAccount.Id);
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var clearedBody = (await cleared.Content.ReadFromJsonAsync<CoHostDto>())!;
        Assert.Equal("ACTIVE", clearedBody.Status);
        Assert.Empty(clearedBody.Permissions);
        await AssertStatus(HttpStatusCode.Forbidden, cohost.GetAsync(guests), "cleared guests");
        access = (await cohost.GetFromJsonAsync<WeddingAccessDto>($"/api/weddings/{wedding.Id}/access"))!;
        Assert.Empty(access.Permissions);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task RevokedMembersStayVisibleToOwnerCannotBeEditedAndUnknownTargetsReturn404() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "state-owner");
        using var cohost = await CoHostPermissionTests.SignIn(factory, "state-cohost");
        var wedding = await CoHostPermissionTests.CreateWedding(owner, "coh-state");
        var ownerAccount = await Me(owner);
        var cohostAccount = await Me(cohost);
        var root = Root(wedding.Id);

        await AddCoHost(owner, wedding.Id, cohostAccount.Email, "GUEST_VIEW", "GIFT_VIEW");
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
        await AssertStatus(HttpStatusCode.Forbidden, cohost.GetAsync($"/api/weddings/{wedding.Id}/access"), "revoked access");
        await AssertStatus(HttpStatusCode.Forbidden, cohost.GetAsync($"/api/weddings/{wedding.Id}"), "revoked workspace");

        var unknown = Guid.NewGuid();
        await AssertStatus(HttpStatusCode.NotFound, owner.GetAsync($"{root}/{unknown}"), "GET unknown");
        await AssertStatus(HttpStatusCode.NotFound, SetPermissions(owner, wedding.Id, unknown, "GUEST_VIEW"), "PUT unknown");
        await AssertStatus(HttpStatusCode.NotFound, owner.DeleteAsync($"{root}/{unknown}"), "DELETE unknown");
        await AssertStatus(HttpStatusCode.NotFound, owner.GetAsync($"{root}/{ownerAccount.Id}"), "GET owner as member");
        await AssertStatus(HttpStatusCode.NotFound, SetPermissions(owner, wedding.Id, ownerAccount.Id, "GUEST_VIEW"), "PUT owner as member");
        await AssertStatus(HttpStatusCode.NotFound, owner.DeleteAsync($"{root}/{ownerAccount.Id}"), "DELETE owner as member");

        var again = await AddCoHost(owner, wedding.Id, cohostAccount.Email);
        Assert.Equal("ACTIVE", again.Status);
        Assert.Empty(again.Permissions);
        Assert.Equal(detail.Id, again.Id);
        Assert.Single(await db.WeddingMembers.AsNoTracking().Where(x => x.WeddingId == wedding.Id).ToListAsync());
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ConcurrentAddOfTheSameCoHostCreatesOneMemberAndNoServerError() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "race-owner");
        using var cohost = await CoHostPermissionTests.SignIn(factory, "race-cohost");
        var wedding = await CoHostPermissionTests.CreateWedding(owner, "coh-race");
        var cohostAccount = await Me(cohost);
        var payload = new AddCoHostInput(cohostAccount.Email, ["GUEST_VIEW"]);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => owner.PostAsJsonAsync(Root(wedding.Id), payload)));
        var codes = responses.Select(x => x.StatusCode).ToArray();

        Assert.Equal(1, codes.Count(x => x == HttpStatusCode.Created));
        Assert.Equal(3, codes.Count(x => x == HttpStatusCode.Conflict));
        Assert.Single(await db.WeddingMembers.AsNoTracking().Where(x => x.WeddingId == wedding.Id).ToListAsync());
        Assert.Equal(1, await db.WeddingMemberPermissions.CountAsync());
        await AssertStatus(HttpStatusCode.OK, cohost.GetAsync($"/api/weddings/{wedding.Id}/guests"), "single grant works");
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task RepeatedRevokeIsIdempotentAndWritesASingleAuditRow() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "idem-owner");
        using var cohost = await CoHostPermissionTests.SignIn(factory, "idem-cohost");
        var wedding = await CoHostPermissionTests.CreateWedding(owner, "coh-idem");
        var cohostAccount = await Me(cohost);
        var root = Root(wedding.Id);

        await AddCoHost(owner, wedding.Id, cohostAccount.Email, "GUEST_VIEW");
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
        var wedding = await CoHostPermissionTests.CreateWedding(owner, "coh-diff");
        var cohostAccount = await Me(cohost);
        var codes = await db.Permissions.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Code);

        await AddCoHost(owner, wedding.Id, cohostAccount.Email, "GUEST_VIEW", "GIFT_VIEW");
        var before = (await db.WeddingMemberPermissions.AsNoTracking().ToListAsync()).ToDictionary(x => codes[x.PermissionId], x => x.GrantedAt);
        await Task.Delay(50);

        var response = await SetPermissions(owner, wedding.Id, cohostAccount.Id, "GUEST_VIEW", "GIFT_EDIT");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var after = (await db.WeddingMemberPermissions.AsNoTracking().ToListAsync()).ToDictionary(x => codes[x.PermissionId], x => x.GrantedAt);
        Assert.Equal(["GIFT_EDIT", "GUEST_VIEW"], after.Keys.Order().ToArray());
        Assert.Equal(before["GUEST_VIEW"], after["GUEST_VIEW"]);
        Assert.True(after["GIFT_EDIT"] > before["GUEST_VIEW"]);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task PermissionMissingFromTheCatalogIsRejectedAndNothingIsPersisted() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "seed-owner");
        using var cohost = await CoHostPermissionTests.SignIn(factory, "seed-cohost");
        var wedding = await CoHostPermissionTests.CreateWedding(owner, "coh-seed");
        var cohostAccount = await Me(cohost);
        var root = Root(wedding.Id);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM inviteme.permissions WHERE code = 'GIFT_EDIT'");

        await AssertStatus(HttpStatusCode.UnprocessableEntity, owner.PostAsJsonAsync(root, new AddCoHostInput(cohostAccount.Email, ["GUEST_VIEW", "GIFT_EDIT"])), "add with missing catalog permission");
        Assert.Equal(0, await db.WeddingMembers.CountAsync(x => x.WeddingId == wedding.Id));
        Assert.Equal(0, await db.WeddingMemberPermissions.CountAsync());
        Assert.Equal(0, await db.AuditLogs.CountAsync(x => x.EntityType == "wedding_members"));

        await AddCoHost(owner, wedding.Id, cohostAccount.Email, "GUEST_VIEW");
        await AssertStatus(HttpStatusCode.UnprocessableEntity, SetPermissions(owner, wedding.Id, cohostAccount.Id, "GIFT_EDIT"), "update with missing catalog permission");
        var stored = (await owner.GetFromJsonAsync<CoHostDto>($"{root}/{cohostAccount.Id}"))!;
        Assert.Equal(["GUEST_VIEW"], stored.Permissions.ToArray());
        Assert.Equal(1, await db.AuditLogs.CountAsync(x => x.EntityType == "wedding_members"));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ConcurrentUpdatesAndRevokeNeverLeaveGrantsOnARevokedMember() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "lock-owner");
        using var cohost = await CoHostPermissionTests.SignIn(factory, "lock-cohost");
        var wedding = await CoHostPermissionTests.CreateWedding(owner, "coh-lock");
        var cohostAccount = await Me(cohost);
        var root = Root(wedding.Id);
        await AddCoHost(owner, wedding.Id, cohostAccount.Email, "GUEST_VIEW");

        var calls = new List<Task<HttpResponseMessage>>
        {
            owner.DeleteAsync($"{root}/{cohostAccount.Id}"),
            SetPermissions(owner, wedding.Id, cohostAccount.Id, "GUEST_VIEW", "GUEST_EDIT"),
            SetPermissions(owner, wedding.Id, cohostAccount.Id, "GIFT_VIEW"),
            SetPermissions(owner, wedding.Id, cohostAccount.Id, "SEATING_VIEW", "SEATING_EDIT")
        };
        var responses = await Task.WhenAll(calls);

        Assert.Equal(HttpStatusCode.NoContent, responses[0].StatusCode);
        Assert.All(responses.Skip(1), r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, $"unexpected {(int)r.StatusCode}"));
        Assert.Equal("REVOKED", (await db.WeddingMembers.AsNoTracking().SingleAsync(x => x.WeddingId == wedding.Id)).Status);
        Assert.Equal(0, await db.WeddingMemberPermissions.CountAsync());
        await AssertStatus(HttpStatusCode.Forbidden, cohost.GetAsync($"/api/weddings/{wedding.Id}/guests"), "revoked member has no access");
    });

    private static string Root(Guid weddingId) => $"/api/weddings/{weddingId}/co-hosts";

    private static async Task<AccountDto> Me(HttpClient client) => (await client.GetFromJsonAsync<AccountDto>("/api/auth/me"))!;

    private static async Task<CoHostDto> AddCoHost(HttpClient owner, Guid weddingId, string email, params string[] permissions)
    {
        var response = await owner.PostAsJsonAsync(Root(weddingId), new AddCoHostInput(email, permissions));
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"add co-host: expected 201 but got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<CoHostDto>())!;
    }

    private static Task<HttpResponseMessage> SetPermissions(HttpClient client, Guid weddingId, Guid coHostUserId, params string[] permissions) =>
        client.PutAsJsonAsync($"{Root(weddingId)}/{coHostUserId}/permissions", new UpdateCoHostPermissionsInput(permissions));

    private static async Task AssertStatus(HttpStatusCode expected, Task<HttpResponseMessage> call, string label)
    {
        var response = await call;
        Assert.True(expected == response.StatusCode, $"{label}: expected {(int)expected} but got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task AssertNotForbidden(Task<HttpResponseMessage> call, string label)
    {
        var response = await call;
        Assert.True(response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized), $"{label}: unexpectedly denied with {(int)response.StatusCode}");
    }
}
