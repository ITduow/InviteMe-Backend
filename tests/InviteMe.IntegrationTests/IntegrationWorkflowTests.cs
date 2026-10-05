using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Guests.Import;
using InviteMe.Application.Features.Invitations.Lifecycle;
using InviteMe.Application.Features.Weddings.Workspace;
using InviteMe.Application.Features.Rsvps.Workflow;
using InviteMe.Application.Features.Seating.Workflow;
using InviteMe.Application.Features.Reception;
using InviteMe.Application.Features.Gifts.Workflow;
using InviteMe.Application.Features.Reports;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.IntegrationTests;

public sealed class IntegrationWorkflowTests
{
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task HappyCaseCreatesEveryBusinessRecordThroughRealHttp() => WithApi(async (db, factory, owner) =>
    {
        var w = await Wedding(owner, "happy", 10); var root = Root(w);
        var rows = new ImportGuests([new("G001", "Family", Email: "family@example.test", MaxPlusOne: 1, Companions: [new("Spouse", "SPOUSE")])]);
        var dry = await Read<ImportResult>(await owner.PostAsJsonAsync(root + "/guests/import", rows with { DryRun = true }));
        Assert.True(dry.DryRun); Assert.Equal(0, await db.WeddingGuests.CountAsync());
        var guest = Assert.Single((await Read<ImportResult>(await owner.PostAsJsonAsync(root + "/guests/import", rows))).Guests);
        var pub = await Publish(owner, w, guest.Id); using var publicClient = factory.CreateHttpsClient(); var url = Public(pub);
        var initial = (await publicClient.GetFromJsonAsync<RsvpDto>(url + "/rsvp"))!;
        var rsvp = await Read<RsvpDto>(await publicClient.PostAsJsonAsync(url + "/rsvp", new SubmitRsvp(initial.Version, "ATTENDING", guest.Participants.Select(x => x.Id).ToArray(), ["Friend"])));
        Assert.Equal(3, rsvp.ConfirmedPartySize); Assert.Equal(3, rsvp.Participants.Count);
        var delivery = await Read<DeliveryDto>(await owner.PostAsJsonAsync(root + $"/invitations/{pub.Invitation.Id}/send", new SendInvitation(rsvp.Version, "EMAIL", Guid.NewGuid())));
        Assert.True(delivery.IsSandbox);
        await using (var worker = new WorkspaceApiFactory(db.Database.GetConnectionString()!, true))
        {
            using var trigger = worker.CreateHttpsClient(); await trigger.GetAsync("/health");
            for (var i = 0; i < 30 && !await db.InvitationDeliveries.AsNoTracking().AnyAsync(x => x.Id == delivery.Id && x.Status == "SENT"); i++) await Task.Delay(200);
            Assert.True(await db.InvitationDeliveries.AsNoTracking().AnyAsync(x => x.Id == delivery.Id && x.Status == "SENT"));
        }
        var table = await Read<TableDto>(await owner.PostAsJsonAsync(root + "/tables", new CreateTable("A1", 3, "ACTIVE")));
        for (var i = 0; i < rsvp.Participants.Count; i++)
        {
            await Read<AssignmentDto>(await owner.PutAsJsonAsync(root + $"/seating/{rsvp.Participants[i].Id}", new AssignSeat(table.Id, table.Seats[i].Id, table.Version)));
            table = Assert.Single((await owner.GetFromJsonAsync<PagedResult<TableDto>>(root + "/tables"))!.Items);
        }
        Assert.Equal(3, table.Occupied); Assert.NotNull(table.ActivatedAt);
        var scan = await Read<ScanResult>(await owner.PostAsJsonAsync(root + "/check-ins/scan", new ScanInvitation(pub.PublicPath.Split('/').Last())));
        Assert.Equal(3, scan.Participants.Count);
        foreach (var p in scan.Participants)
        {
            var receipt = await Read<CheckInDto>(await owner.PostAsJsonAsync(root + "/check-ins", new CheckInInput(p.Id)));
            Assert.Equal(receipt.Id, (await Read<CheckInDto>(await owner.PostAsJsonAsync(root + "/check-ins", new CheckInInput(p.Id)))).Id);
            Assert.True(receipt.CheckedInAt > DateTimeOffset.MinValue);
        }
        var giftInput = new RecordGift(guest.Id, null, 500000, Guid.NewGuid(), Message: "Congratulations");
        var gift = await Read<GiftDto>(await owner.PostAsJsonAsync(root + "/gifts", giftInput));
        Assert.Equal(gift.Id, (await Read<GiftDto>(await owner.PostAsJsonAsync(root + "/gifts", giftInput))).Id);
        var report = (await owner.GetFromJsonAsync<WeddingReportDto>(root + "/reports/summary?includeGifts=true"))!;
        Assert.Equal(2, report.EstimatedHeadcount); Assert.Equal(3, report.ConfirmedHeadcount);
        Assert.Equal(3, report.SeatedHeadcount); Assert.Equal(3, report.CheckedInHeadcount); Assert.Equal(7, report.RemainingCapacity);
        Assert.Equal(500000, Assert.Single(report.Gifts!).CompletedAmount);
        Assert.Equal(1, await db.RsvpHistory.CountAsync()); Assert.Equal(3, await db.SeatingChangeLogs.CountAsync());
        Assert.Equal(3, await db.CheckIns.CountAsync()); Assert.Equal(1, await db.Gifts.CountAsync());
        Assert.True(await db.AuditLogs.AnyAsync(x => x.ActorType == "WEDDING_GUEST" && x.Action == "RSVP_SUBMITTED"));
        Assert.False(db.Database.HasPendingModelChanges());
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ConcurrentPartiesCannotExceedCapacityAndWaitlistCanBePromoted() => WithApi(async (db, factory, owner) =>
    {
        var w = await Wedding(owner, "race", 1); var root = Root(w);
        var a = await Guest(owner, w, "A"); var b = await Guest(owner, w, "B");
        var pa = await Publish(owner, w, a.Id); var pb = await Publish(owner, w, b.Id);
        using var guestClient = factory.CreateHttpsClient();
        var responses = await Task.WhenAll(guestClient.PostAsJsonAsync(Public(pa) + "/rsvp", new SubmitRsvp(pa.Invitation.Version, "ATTENDING", [a.Participants[0].Id])),
            guestClient.PostAsJsonAsync(Public(pb) + "/rsvp", new SubmitRsvp(pb.Invitation.Version, "ATTENDING", [b.Participants[0].Id])));
        var states = await Task.WhenAll(responses.Select(Read<RsvpDto>));
        var attending = Assert.Single(states, x => x.Status == "ATTENDING"); var waiting = Assert.Single(states, x => x.WaitlistedPartySize == 1);
        Assert.Equal(1, (await owner.GetFromJsonAsync<WeddingReportDto>(root + "/reports/summary"))!.ConfirmedHeadcount);
        var entry = Assert.Single((await owner.GetFromJsonAsync<PagedResult<WaitlistDto>>(root + "/waitlist"))!.Items);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PostAsync(root + $"/waitlist/{entry.Id}/promote", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PutAsJsonAsync(root + $"/invitations/{attending.InvitationId}/rsvp", new SubmitRsvp(attending.Version - 1, "DECLINED", []))).StatusCode);
        await Read<RsvpDto>(await owner.PutAsJsonAsync(root + $"/invitations/{attending.InvitationId}/rsvp", new SubmitRsvp(attending.Version, "DECLINED", [])));
        var promoted = await Read<RsvpDto>(await owner.PostAsync(root + $"/waitlist/{entry.Id}/promote", null));
        Assert.Equal(waiting.InvitationId, promoted.InvitationId); Assert.Equal("ATTENDING", promoted.Status);
        Assert.Empty((await owner.GetFromJsonAsync<PagedResult<WaitlistDto>>(root + "/waitlist"))!.Items);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PostAsync(root + $"/waitlist/{entry.Id}/promote", null)).StatusCode);
        Assert.Equal(4, await db.RsvpHistory.CountAsync());
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ImportIsAtomicAndRejectsInvalidOrDuplicateRows() => WithApi(async (db, factory, owner) =>
    {
        var w = await Wedding(owner, "import", 5); var url = Root(w) + "/guests/import";
        foreach (var input in new object[] { new ImportGuests([]), new ImportGuests([new("a", "A"), new("A", "B")]),
            new ImportGuests([new("bad", " ")]), new ImportGuests([new("bad", "A", MaxPlusOne: -1)]),
            new { rows = new object?[] { null } }, new { rows = new[] { new { guestCode = "X", fullName = "X", status = "ATTENDING" } } },
            new ImportGuests(Enumerable.Range(0, 501).Select(i => new GuestInput("G" + i, "Name")).ToArray()) })
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(url, input)).StatusCode);
        Assert.Equal(0, await db.WeddingGuests.CountAsync()); await Guest(owner, w, "EXISTS");
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync(url, new ImportGuests([new("NEW", "New"), new("exists", "Duplicate")]))).StatusCode);
        Assert.Equal(1, await db.WeddingGuests.CountAsync()); Assert.Equal(1, await db.GuestParticipants.CountAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync(Root(w) + "/participants?page=2147483647")).StatusCode);
        using var outsider = await SignIn(factory, "outsider");
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync(url, new ImportGuests([new("X", "X")]))).StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task RsvpEnforcesPartyScopePlusOneDeadlineAndTokenRevocation() => WithApi(async (db, factory, owner) =>
    {
        var w = await Wedding(owner, "rsvp", 2); var a = await Guest(owner, w, "A"); var b = await Guest(owner, w, "B");
        var p = await Publish(owner, w, a.Id); using var c = factory.CreateHttpsClient(); var url = Public(p) + "/rsvp";
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync(url, new SubmitRsvp(p.Invitation.Version, "ATTENDING", [b.Participants[0].Id]))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.PostAsJsonAsync(url, new SubmitRsvp(p.Invitation.Version, "ATTENDING", [a.Participants[0].Id], ["Unallowed"]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync(url, new SubmitRsvp(p.Invitation.Version, "DECLINED", [a.Participants[0].Id]))).StatusCode);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inviteme.wedding_settings SET rsvp_deadline={deadline} WHERE wedding_id={w.Id}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.PostAsJsonAsync(url, new SubmitRsvp(p.Invitation.Version, "ATTENDING", [a.Participants[0].Id]))).StatusCode);
        Assert.Equal(0, await db.Rsvps.CountAsync());
        await Read<InvitationDto>(await owner.PostAsJsonAsync(Root(w) + $"/invitations/{p.Invitation.Id}/revoke", new InvitationAction(p.Invitation.Version)));
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync(Public(p) + "/gifts", new PublicGift(100, Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/public/invitations/invalid/rsvp")).StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task SeatingVersionsCapacityMovesAndCheckInLockAreEnforced() => WithApi(async (db, factory, owner) =>
    {
        var w = await Wedding(owner, "seating", 5); var root = Root(w); var a = await Guest(owner, w, "A"); var b = await Guest(owner, w, "B");
        var pa = await Publish(owner, w, a.Id); var pb = await Publish(owner, w, b.Id);
        await Attend(owner, pa, a); await Attend(owner, pb, b);
        var table = await Read<TableDto>(await owner.PostAsJsonAsync(root + "/tables", new CreateTable("T1", 1)));
        var aid = a.Participants[0].Id; var bid = b.Participants[0].Id;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PutAsJsonAsync(root + $"/seating/{aid}", new AssignSeat(table.Id, null, table.Version))).StatusCode);
        table = await Read<TableDto>(await owner.PutAsJsonAsync(root + $"/tables/{table.Id}", new UpdateTable(table.Version, 1, "ACTIVE")));
        var races = await Task.WhenAll(owner.PutAsJsonAsync(root + $"/seating/{aid}", new AssignSeat(table.Id, table.Seats[0].Id, table.Version)),
            owner.PutAsJsonAsync(root + $"/seating/{bid}", new AssignSeat(table.Id, table.Seats[0].Id, table.Version)));
        var assignment = await Read<AssignmentDto>(Assert.Single(races, x => x.IsSuccessStatusCode));
        Assert.Single(races, x => x.StatusCode == HttpStatusCode.Conflict);
        table = Assert.Single((await owner.GetFromJsonAsync<PagedResult<TableDto>>(root + "/tables"))!.Items);
        var other = assignment.ParticipantId == aid ? bid : aid;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PutAsJsonAsync(root + $"/seating/{other}", new AssignSeat(table.Id, null, table.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PutAsJsonAsync(root + $"/tables/{table.Id}", new UpdateTable(table.Version, 1, "INACTIVE"))).StatusCode);
        var invitation = assignment.ParticipantId == aid ? pa : pb;
        var state = (await owner.GetFromJsonAsync<RsvpDto>(root + $"/invitations/{invitation.Invitation.Id}/rsvp"))!;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PutAsJsonAsync(root + $"/invitations/{state.InvitationId}/rsvp", new SubmitRsvp(state.Version, "DECLINED", []))).StatusCode);
        var second = await Read<TableDto>(await owner.PostAsJsonAsync(root + "/tables", new CreateTable("T2", 2, "ACTIVE")));
        assignment = await Read<AssignmentDto>(await owner.PutAsJsonAsync(root + $"/seating/{assignment.ParticipantId}", new AssignSeat(second.Id, second.Seats[0].Id, second.Version, assignment.Version)));
        Assert.Equal(2, assignment.Version);
        second = (await owner.GetFromJsonAsync<PagedResult<TableDto>>(root + "/tables"))!.Items.Single(x => x.Id == second.Id);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync(root + $"/seating/{assignment.ParticipantId}/unassign", new UnassignSeat(assignment.Version, second.Version))).StatusCode);
        var checks = await Task.WhenAll(owner.PostAsJsonAsync(root + "/check-ins", new CheckInInput(assignment.ParticipantId)), owner.PostAsJsonAsync(root + "/check-ins", new CheckInInput(assignment.ParticipantId)));
        Assert.Equal((await Read<CheckInDto>(checks[0])).Id, (await Read<CheckInDto>(checks[1])).Id);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PutAsJsonAsync(root + $"/invitations/{state.InvitationId}/rsvp", new SubmitRsvp(state.Version, "DECLINED", []))).StatusCode);
        Assert.Equal(1, await db.CheckIns.CountAsync()); Assert.Equal(3, await db.SeatingChangeLogs.CountAsync());
        Assert.Contains(await db.SeatingChangeLogs.ToListAsync(), x => x.Action == "UNASSIGN" && x.AssignmentId == null);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task WalkinsReserveCapacityOnlyOnCheckInAndProtectWorkspaceCapacity() => WithApi(async (db, factory, owner) =>
    {
        var w = await Wedding(owner, "walkins", 2); var root = Root(w);
        var walk = await Read<WalkinDto>(await owner.PostAsJsonAsync(root + "/walk-ins", new WalkinInput("Walk in", 2)));
        Assert.Equal(0, await db.WeddingGuests.CountAsync());
        await Read<CheckInDto>(await owner.PostAsJsonAsync(root + "/check-ins", new CheckInInput(WalkinId: walk.Id)));
        var extra = await Read<WalkinDto>(await owner.PostAsJsonAsync(root + "/walk-ins", new WalkinInput("Extra")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PostAsJsonAsync(root + "/check-ins", new CheckInInput(WalkinId: extra.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PutAsJsonAsync(root + "/settings", Settings(w.Slug, 1) with { ExpectedVersion = w.Version })).StatusCode);
        var a = await Guest(owner, w, "A"); var pa = await Publish(owner, w, a.Id);
        var waiting = await Attend(owner, pa, a); Assert.Equal(1, waiting.WaitlistedPartySize); Assert.Equal(0, waiting.ConfirmedPartySize);
        var report = (await owner.GetFromJsonAsync<WeddingReportDto>(root + "/reports/summary"))!;
        Assert.Equal(2, report.CheckedInWalkinHeadcount); Assert.Equal(0, report.RemainingCapacity); Assert.Null(report.Gifts);
        await Read<GiftDto>(await owner.PostAsJsonAsync(root + "/gifts", new RecordGift(null, walk.Id, 100, Guid.NewGuid())));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task DeclinedGuestGiftIntentNeedsConfirmationAndCurrenciesStaySeparate() => WithApi(async (db, factory, owner) =>
    {
        var w = await Wedding(owner, "gifts", 2); var root = Root(w); var a = await Guest(owner, w, "A"); var p = await Publish(owner, w, a.Id);
        using var c = factory.CreateHttpsClient(); var url = Public(p);
        await Read<RsvpDto>(await c.PostAsJsonAsync(url + "/rsvp", new SubmitRsvp(p.Invitation.Version, "DECLINED", [])));
        var input = new PublicGift(200000, Guid.NewGuid(), Message: "Good wishes");
        var gift = await Read<GiftDto>(await c.PostAsJsonAsync(url + "/gifts", input)); Assert.Equal("PENDING", gift.Status); Assert.Null(gift.ReceivedBy);
        Assert.Equal(gift.Id, (await Read<GiftDto>(await c.PostAsJsonAsync(url + "/gifts", input))).Id);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync(url + "/gifts", input with { Amount = 300000 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync(url + "/gifts", new { amount = 100, idempotencyKey = Guid.NewGuid(), status = "COMPLETED" })).StatusCode);
        var pending = (await owner.GetFromJsonAsync<WeddingReportDto>(root + "/reports/summary?includeGifts=true"))!;
        Assert.Equal(200000, Assert.Single(pending.Gifts!).PendingAmount); Assert.Equal(0, pending.Gifts![0].CompletedAmount);
        var confirmed = await Read<GiftDto>(await owner.PostAsync(root + $"/gifts/{gift.Id}/confirm", null)); Assert.Equal("COMPLETED", confirmed.Status); Assert.NotNull(confirmed.ReceivedBy);
        Assert.Equal(confirmed, await Read<GiftDto>(await owner.PostAsync(root + $"/gifts/{gift.Id}/confirm", null)));
        Assert.Equal(gift.Id, (await Read<GiftDto>(await c.PostAsJsonAsync(url + "/gifts", input))).Id);
        await Read<GiftDto>(await owner.PostAsJsonAsync(root + "/gifts", new RecordGift(a.Id, null, 20, Guid.NewGuid(), "USD")));
        var totals = (await owner.GetFromJsonAsync<WeddingReportDto>(root + "/reports/summary?includeGifts=true"))!.Gifts!;
        Assert.Equal(2, totals.Count); Assert.Equal(20, totals.Single(x => x.Currency == "USD").CompletedAmount); Assert.Equal(200000, totals.Single(x => x.Currency == "VND").CompletedAmount);
        Assert.Equal(2, await db.Gifts.CountAsync());
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task PermissionsAndTenantIsolationCoverAllWorkflowSlices() => WithApi(async (db, factory, owner) =>
    {
        var w = await Wedding(owner, "scope", 5); var otherWedding = await Wedding(owner, "other", 5); var root = Root(w);
        var foreign = await Guest(owner, otherWedding, "FOREIGN"); var local = await Guest(owner, w, "LOCAL"); var p = await Publish(owner, w, local.Id); await Attend(owner, p, local);
        var foreignTable = await Read<TableDto>(await owner.PostAsJsonAsync(Root(otherWedding) + "/tables", new CreateTable("T", 2, "ACTIVE")));
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PutAsJsonAsync(root + $"/seating/{local.Participants[0].Id}", new AssignSeat(foreignTable.Id, null, foreignTable.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync(root + "/check-ins", new CheckInInput(foreign.Participants[0].Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync(root + "/gifts", new RecordGift(foreign.Id, null, 100, Guid.NewGuid()))).StatusCode);
        using var outsider = await SignIn(factory, "outsider"); using var anonymous = factory.CreateHttpsClient();
        foreach (var suffix in new[] { "/participants", "/waitlist", "/tables", "/seating", "/check-ins", "/gifts", "/reports/summary" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(root + suffix)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(root + suffix)).StatusCode);
        }
        using var cohost = await SignIn(factory, "cohost"); var uid = (await cohost.GetFromJsonAsync<AccountDto>("/api/auth/me"))!.Id; var member = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inviteme.wedding_members(id,wedding_id,user_id) VALUES ({member},{w.Id},{uid})");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inviteme.wedding_member_permissions(member_id,permission_id) SELECT {member},id FROM inviteme.permissions WHERE code IN ('ANALYTICS_VIEW','GUEST_EDIT','SEATING_VIEW')");
        Assert.Null((await cohost.GetFromJsonAsync<WeddingReportDto>(root + "/reports/summary"))!.Gifts);
        Assert.Equal(HttpStatusCode.Forbidden, (await cohost.GetAsync(root + "/reports/summary?includeGifts=true")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cohost.GetAsync(root + "/tables")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cohost.PostAsJsonAsync(root + "/tables", new CreateTable("DENIED", 2))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cohost.PostAsJsonAsync(root + "/check-ins", new CheckInInput(local.Participants[0].Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cohost.PostAsJsonAsync(root + "/gifts", new RecordGift(local.Id, null, 100, Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cohost.PostAsJsonAsync(root + "/check-ins/scan", new ScanInvitation(p.PublicPath.Split('/').Last()))).StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task WholePartyWaitlistResubmissionRemainsAtomic() => WithApi(async (db, factory, owner) =>
    {
        var w = await Wedding(owner, "party", 1); var root = Root(w);
        var family = await Read<ImportedGuestDto>(await owner.PostAsJsonAsync(root + "/guests", new GuestInput("F", "Family", Companions: [new("Child", "CHILD")])));
        var p = await Publish(owner, w, family.Id); using var c = factory.CreateHttpsClient();
        var waiting = await Read<RsvpDto>(await c.PostAsJsonAsync(Public(p) + "/rsvp", new SubmitRsvp(p.Invitation.Version, "ATTENDING", family.Participants.Select(x => x.Id).ToArray())));
        Assert.Equal(2, waiting.WaitlistedPartySize); Assert.Equal(0, waiting.ConfirmedPartySize); Assert.Equal("PENDING", waiting.Status);
        var table = await Read<TableDto>(await owner.PostAsJsonAsync(root + "/tables", new CreateTable("T", 2, "ACTIVE")));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PutAsJsonAsync(root + $"/seating/{family.Participants[0].Id}", new AssignSeat(table.Id, null, table.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PostAsJsonAsync(root + "/check-ins", new CheckInInput(family.Participants[0].Id))).StatusCode);
        waiting = await Read<RsvpDto>(await c.PostAsJsonAsync(Public(p) + "/rsvp", new SubmitRsvp(waiting.Version, "ATTENDING", family.Participants.Select(x => x.Id).ToArray())));
        Assert.Equal(1, await db.WaitlistEntries.CountAsync(x => x.Status == "WAITING"));
        Assert.Equal(1, await db.WaitlistEntries.CountAsync(x => x.Status == "CANCELLED"));
        var attending = await Read<RsvpDto>(await c.PostAsJsonAsync(Public(p) + "/rsvp", new SubmitRsvp(waiting.Version, "ATTENDING", [family.Participants[0].Id])));
        Assert.Equal(1, attending.ConfirmedPartySize); Assert.Equal(0, attending.WaitlistedPartySize);
        Assert.Equal(0, await db.WaitlistEntries.CountAsync(x => x.Status == "WAITING"));
        Assert.Equal(3, await db.RsvpHistory.CountAsync());
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ExpiredAndClosedWeddingTokensCannotMutateAnyPublicSlice() => WithApi(async (db, factory, owner) =>
    {
        var w = await Wedding(owner, "token", 2); var g = await Guest(owner, w, "G"); var p = await Publish(owner, w, g.Id);
        using var c = factory.CreateHttpsClient(); var expiry = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inviteme.invitations SET expires_at={expiry} WHERE id={p.Invitation.Id}");
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(Public(p) + "/rsvp")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync(Public(p) + "/gifts", new PublicGift(100, Guid.NewGuid()))).StatusCode);
        var future = DateTimeOffset.UtcNow.AddDays(90);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inviteme.invitations SET expires_at={future} WHERE id={p.Invitation.Id}");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inviteme.weddings SET status='ARCHIVED' WHERE id={w.Id}");
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(Public(p) + "/rsvp")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync(Public(p) + "/gifts", new PublicGift(100, Guid.NewGuid()))).StatusCode);
        Assert.Equal(0, await db.Gifts.CountAsync()); Assert.Equal(0, await db.Rsvps.CountAsync());
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task EmptyWeddingReportReturnsZeroAndRejectsInvalidFinancialInputs() => WithApi(async (db, factory, owner) =>
    {
        var w = await Wedding(owner, "empty", 2); var root = Root(w);
        var report = (await owner.GetFromJsonAsync<WeddingReportDto>(root + "/reports/summary?includeGifts=true"))!;
        Assert.Equal(0, report.ConfirmedHeadcount); Assert.Equal(0, report.EstimatedHeadcount); Assert.Empty(report.Gifts!);
        Assert.Equal(2, report.RemainingCapacity);
        var g = await Guest(owner, w, "G");
        foreach (var input in new[] { new RecordGift(g.Id, null, -1, Guid.NewGuid()), new RecordGift(g.Id, null, 1.001m, Guid.NewGuid()),
            new RecordGift(g.Id, null, 10, Guid.Empty), new RecordGift(g.Id, null, 10, Guid.NewGuid(), "vnd"),
            new RecordGift(g.Id, Guid.NewGuid(), 10, Guid.NewGuid()) })
            Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(root + "/gifts", input)).StatusCode);
        Assert.Equal(0, await db.Gifts.CountAsync());
    });

    private static Task WithApi(Func<InviteMeDbContext, WorkspaceApiFactory, HttpClient, Task> test) => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync(); await using var factory = new WorkspaceApiFactory(db.Database.GetConnectionString()!);
        using var owner = await SignIn(factory, "owner"); await test(db, factory, owner);
    });
    private static WorkspaceInput Settings(string slug, int capacity) => new("Integration wedding", slug, capacity, "Asia/Ho_Chi_Minh",
        DateTimeOffset.UtcNow.AddDays(60), DateTimeOffset.UtcNow.AddDays(60).AddHours(3), DateTimeOffset.UtcNow.AddDays(50), "Venue", "Address");
    private static async Task<WorkspaceDto> Wedding(HttpClient c, string slug, int capacity) => await Read<WorkspaceDto>(await c.PostAsJsonAsync("/api/weddings", Settings(slug, capacity)));
    private static string Root(WorkspaceDto w) => $"/api/weddings/{w.Id}";
    private static string Public(PublicationDto p) => "/api/public/invitations/" + p.PublicPath.Split('/').Last();
    private static async Task<ImportedGuestDto> Guest(HttpClient c, WorkspaceDto w, string code) => await Read<ImportedGuestDto>(await c.PostAsJsonAsync(Root(w) + "/guests", new GuestInput(code, code, Email: code + "@example.test")));
    private static async Task<RsvpDto> Attend(HttpClient c, PublicationDto p, ImportedGuestDto g) => await Read<RsvpDto>(await c.PutAsJsonAsync($"/api/weddings/{p.Invitation.WeddingId}/invitations/{p.Invitation.Id}/rsvp", new SubmitRsvp(p.Invitation.Version, "ATTENDING", g.Participants.Select(x => x.Id).ToArray())));
    private static async Task<PublicationDto> Publish(HttpClient c, WorkspaceDto w, Guid guest)
    {
        var template = (await c.GetFromJsonAsync<List<TemplateDto>>("/api/invitation-templates"))!.Single(x => x.Slug == "inviteme-classic");
        var x = await Read<InvitationDto>(await c.PostAsJsonAsync(Root(w) + "/invitations", new CreateInvitation(guest, new(template.Id, "One", "Two", "Welcome"))));
        foreach (var action in new[] { "preview", "review", "approve" }) x = await Read<InvitationDto>(await c.PostAsJsonAsync(Root(w) + $"/invitations/{x.Id}/{action}", new InvitationAction(x.Version)));
        return await Read<PublicationDto>(await c.PostAsJsonAsync(Root(w) + $"/invitations/{x.Id}/publish", new InvitationAction(x.Version, DateTimeOffset.UtcNow.AddDays(90))));
    }
    private static async Task<T> Read<T>(HttpResponseMessage response)
    { Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}"); return (await response.Content.ReadFromJsonAsync<T>())!; }
    private static async Task<HttpClient> SignIn(WorkspaceApiFactory factory, string name)
    {
        var c = factory.CreateHttpsClient(); const string password = "Test-only-Pass123!";
        await Read<AccountDto>(await c.PostAsJsonAsync("/api/auth/register", new RegisterAccount(name + "@example.test", password, name)));
        var token = await Read<TokenDto>(await c.PostAsJsonAsync("/api/auth/login", new LoginAccount(name + "@example.test", password)));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken); return c;
    }
}
