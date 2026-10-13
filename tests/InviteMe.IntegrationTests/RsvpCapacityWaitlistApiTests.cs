using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Guests.Import;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Invitations.Lifecycle;
using InviteMe.Application.Features.Reports;
using InviteMe.Application.Features.Rsvps.Workflow;
using InviteMe.Application.Features.Weddings.Workspace;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.IntegrationTests;

/// <summary>RSVP-03: capacity gate, waitlist and promotion through real HTTP against real PostgreSQL.</summary>
public sealed class RsvpCapacityWaitlistApiTests
{
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task CapacityGateConfirmsWhatFitsAndWaitlistsTheRest() => WithApi(async (db, factory, owner) =>
    {
        var w = await Wedding(owner, "rsvp03-gate", 3); var root = Root(w);
        var a = await Family(owner, w, "A"); var b = await Family(owner, w, "B"); var c = await Single(owner, w, "C");
        var pa = await Publish(owner, w, a.Id); var pb = await Publish(owner, w, b.Id); var pc = await Publish(owner, w, c.Id);
        using var guest = factory.CreateHttpsClient();

        // 1. Fits capacity -> confirmed (2/3).
        var ra = await Created(await guest.PostAsJsonAsync(Public(pa), new SubmitRsvp(pa.Invitation.Version, "ATTENDING", Ids(a))));
        Assert.Equal(("ATTENDING", 2, 0, RsvpCapacityOutcome.Confirmed), (ra.Status, ra.ConfirmedPartySize, ra.WaitlistedPartySize, ra.CapacityOutcome));
        // 2. Exactly fills the last slot -> confirmed (3/3).
        var rc = await Created(await guest.PostAsJsonAsync(Public(pc), new SubmitRsvp(pc.Invitation.Version, "ATTENDING", Ids(c))));
        Assert.Equal(("ATTENDING", 1, RsvpCapacityOutcome.Confirmed), (rc.Status, rc.ConfirmedPartySize, rc.CapacityOutcome));
        // 3. Exceeds remaining capacity -> valid 201 outcome: the whole party waits, nothing is confirmed.
        var rb = await Created(await guest.PostAsJsonAsync(Public(pb), new SubmitRsvp(pb.Invitation.Version, "ATTENDING", Ids(b))));
        Assert.Equal(("PENDING", 0, 2, RsvpCapacityOutcome.Waitlisted), (rb.Status, rb.ConfirmedPartySize, rb.WaitlistedPartySize, rb.CapacityOutcome));
        Assert.All(rb.Participants, p => Assert.Equal("WAITLISTED", p.AttendanceStatus));
        var entry = Assert.Single((await owner.GetFromJsonAsync<PagedResult<WaitlistDto>>(root + "/waitlist"))!.Items);
        Assert.Equal((b.Id, 2, "WAITING", WaitlistKind.FullParty), (entry.GuestId, entry.RequestedSlots, entry.Status, entry.Kind));

        // 5. Waitlisted people do not consume confirmed capacity.
        var report = (await owner.GetFromJsonAsync<WeddingReportDto>(root + "/reports/summary"))!;
        Assert.Equal((3L, 2L, 0), (report.ConfirmedHeadcount, report.WaitlistedHeadcount, report.RemainingCapacity));
        Assert.Equal(3, await Confirmed(db, w.Id));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ConfirmedPartyKeepsItsPlacesWhenOnlyTheExtraSlotWaitsThenIsPromoted() => WithApi(async (db, factory, owner) =>
    {
        var w = await Wedding(owner, "rsvp03-partial", 2); var root = Root(w);
        var a = await Family(owner, w, "A"); var c = await Single(owner, w, "C");
        var pa = await Publish(owner, w, a.Id); var pc = await Publish(owner, w, c.Id);
        using var guest = factory.CreateHttpsClient();
        var main = a.Participants.Single(x => x.Type == "PRIMARY").Id; var spouse = a.Participants.Single(x => x.Type != "PRIMARY").Id;
        var first = await Created(await guest.PostAsJsonAsync(Public(pa), new SubmitRsvp(pa.Invitation.Version, "ATTENDING", [main])));
        var rc = await Created(await guest.PostAsJsonAsync(Public(pc), new SubmitRsvp(pc.Invitation.Version, "ATTENDING", Ids(c))));
        Assert.Equal(2, await Confirmed(db, w.Id));
        var frozen = await HistoryFingerprint(db); var historyCount = await db.RsvpHistory.CountAsync();

        // 4. Confirmed guest asks +1 at full capacity: main stays confirmed, only the spouse waits.
        var partial = await Read<RsvpDto>(await guest.PutAsJsonAsync(Public(pa), new SubmitRsvp(first.Version, "ATTENDING", [main, spouse])));
        Assert.Equal(("ATTENDING", 1, 1, RsvpCapacityOutcome.PartiallyWaitlisted), (partial.Status, partial.ConfirmedPartySize, partial.WaitlistedPartySize, partial.CapacityOutcome));
        Assert.Equal("ATTENDING", partial.Participants.Single(x => x.Id == main).AttendanceStatus);
        Assert.Equal("WAITLISTED", partial.Participants.Single(x => x.Id == spouse).AttendanceStatus);
        Assert.True(partial.Version > first.Version);
        var entry = Assert.Single((await owner.GetFromJsonAsync<PagedResult<WaitlistDto>>(root + "/waitlist"))!.Items);
        Assert.Equal((a.Id, 1, WaitlistKind.AdditionalMember), (entry.GuestId, entry.RequestedSlots, entry.Kind));
        Assert.Equal(2, await Confirmed(db, w.Id));
        // The confirmed RSVP did not change, so no RSVP history row is invented; the waitlist change is audited instead.
        Assert.Equal(historyCount, await db.RsvpHistory.CountAsync());
        Assert.True(await db.AuditLogs.AnyAsync(x => x.Action == "RSVP_WAITLIST_UPDATED" && x.ActorType == "WEDDING_GUEST"));

        // Identical resubmission: no-op, same version, same entry (queue position kept).
        var same = await Read<RsvpDto>(await guest.PutAsJsonAsync(Public(pa), new SubmitRsvp(partial.Version, "ATTENDING", [spouse, main])));
        Assert.Equal(partial.Version, same.Version);
        Assert.Equal(entry.Id, Assert.Single((await owner.GetFromJsonAsync<PagedResult<WaitlistDto>>(root + "/waitlist"))!.Items).Id);

        // No vacancy -> promotion refused and nothing changes.
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "CAPACITY_FULL", await owner.PostAsync(root + $"/waitlist/{entry.Id}/promote", null));

        // 11. A confirmed change still appends immutable history; it releases a place but never auto-promotes.
        await Read<RsvpDto>(await guest.PutAsJsonAsync(Public(pc), new SubmitRsvp(rc.Version, "DECLINED", [])));
        Assert.Equal(historyCount + 1, await db.RsvpHistory.CountAsync());
        Assert.True(await db.GuestParticipants.AnyAsync(x => x.Id == spouse && x.AttendanceStatus == "WAITLISTED"));

        // 8 + 9. Released capacity allows the explicit promotion; RSVP, participants, waitlist and history move together.
        var promoted = await Read<RsvpDto>(await owner.PostAsync(root + $"/waitlist/{entry.Id}/promote", null));
        Assert.Equal(("ATTENDING", 2, 0, RsvpCapacityOutcome.Confirmed), (promoted.Status, promoted.ConfirmedPartySize, promoted.WaitlistedPartySize, promoted.CapacityOutcome));
        var stored = await db.WaitlistEntries.AsNoTracking().SingleAsync(x => x.Id == entry.Id);
        Assert.Equal("PROMOTED", stored.Status); Assert.NotNull(stored.PromotedAt);
        var last = await db.RsvpHistory.AsNoTracking().OrderByDescending(x => x.ChangedAt).ThenByDescending(x => x.Id).FirstAsync();
        Assert.Equal(("ATTENDING", 1, "ATTENDING", 2, "WAITLIST_PROMOTED", "USER"),
            (last.OldStatus, last.OldConfirmedPartySize!.Value, last.NewStatus, last.NewConfirmedPartySize, last.Reason, last.ActorType));
        Assert.Equal(2, await Confirmed(db, w.Id));

        // 10. Already promoted -> cannot be promoted twice; nothing is written.
        var count = await db.RsvpHistory.CountAsync();
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "WAITLIST_NOT_WAITING", await owner.PostAsync(root + $"/waitlist/{entry.Id}/promote", null));
        Assert.Equal(count, await db.RsvpHistory.CountAsync());
        await AssertProblem(HttpStatusCode.NotFound, "WAITLIST_NOT_FOUND", await owner.PostAsync(root + $"/waitlist/{Guid.NewGuid()}/promote", null));
        // Earlier history rows are untouched (RSVP-02 append-only).
        Assert.StartsWith(frozen, await HistoryFingerprint(db));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ConcurrentRequestsForTheLastSlotsNeverOverbook() => WithApi(async (db, factory, owner) =>
    {
        // 6. architecture.md acceptance: capacity 150 with 148 attending, two independent +2 requests at the same time.
        var w = await Wedding(owner, "rsvp03-race", 150);
        await Fill(db, await Single(owner, w, "FILL"), 148);
        var a = await Family(owner, w, "A"); var b = await Family(owner, w, "B");
        var pa = await Publish(owner, w, a.Id); var pb = await Publish(owner, w, b.Id);
        using var ca = factory.CreateHttpsClient(); using var cb = factory.CreateHttpsClient();
        var responses = await Task.WhenAll(ca.PostAsJsonAsync(Public(pa), new SubmitRsvp(pa.Invitation.Version, "ATTENDING", Ids(a))),
            cb.PostAsJsonAsync(Public(pb), new SubmitRsvp(pb.Invitation.Version, "ATTENDING", Ids(b))));
        var states = await Task.WhenAll(responses.Select(Created));
        Assert.Single(states, x => x.CapacityOutcome == RsvpCapacityOutcome.Confirmed && x.ConfirmedPartySize == 2);
        Assert.Single(states, x => x.CapacityOutcome == RsvpCapacityOutcome.Waitlisted && x.WaitlistedPartySize == 2);
        Assert.Equal(150, await Confirmed(db, w.Id));
        Assert.Equal(1, await db.WaitlistEntries.CountAsync(x => x.WeddingId == w.Id && x.Status == "WAITING"));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ConcurrentPromotionsCannotOverfillCapacity() => WithApi(async (db, factory, owner) =>
    {
        // 7. Capacity 1: X holds the place, A and B wait; X leaves, then both entries are promoted at the same time.
        var w = await Wedding(owner, "rsvp03-promote", 1); var root = Root(w);
        var x = await Single(owner, w, "X"); var a = await Single(owner, w, "A"); var b = await Single(owner, w, "B");
        var px = await Publish(owner, w, x.Id); var pa = await Publish(owner, w, a.Id); var pb = await Publish(owner, w, b.Id);
        using var guest = factory.CreateHttpsClient();
        var rx = await Created(await guest.PostAsJsonAsync(Public(px), new SubmitRsvp(px.Invitation.Version, "ATTENDING", Ids(x))));
        await Created(await guest.PostAsJsonAsync(Public(pa), new SubmitRsvp(pa.Invitation.Version, "ATTENDING", Ids(a))));
        await Created(await guest.PostAsJsonAsync(Public(pb), new SubmitRsvp(pb.Invitation.Version, "ATTENDING", Ids(b))));
        var entries = (await owner.GetFromJsonAsync<PagedResult<WaitlistDto>>(root + "/waitlist"))!.Items;
        Assert.Equal(2, entries.Count);
        Assert.Equal(new[] { a.Id, b.Id }, entries.Select(e => e.GuestId).ToArray()); // request order
        await Read<RsvpDto>(await guest.PutAsJsonAsync(Public(px), new SubmitRsvp(rx.Version, "DECLINED", [])));

        using var second = await SignIn(factory, "owner", register: false); // independent session of the same host
        var races = await Task.WhenAll(owner.PostAsync(root + $"/waitlist/{entries[0].Id}/promote", null), second.PostAsync(root + $"/waitlist/{entries[1].Id}/promote", null));
        Assert.Single(races, r => r.StatusCode == HttpStatusCode.OK);
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "CAPACITY_FULL", Assert.Single(races, r => r.StatusCode != HttpStatusCode.OK));
        Assert.Equal(1, await Confirmed(db, w.Id));
        Assert.Equal(1, await db.WaitlistEntries.CountAsync(e => e.WeddingId == w.Id && e.Status == "PROMOTED"));
        Assert.Equal(1, await db.WaitlistEntries.CountAsync(e => e.WeddingId == w.Id && e.Status == "WAITING"));

        // The winner leaves again; the remaining entry promoted twice at the same time is applied exactly once.
        var winner = (await db.WaitlistEntries.AsNoTracking().SingleAsync(e => e.WeddingId == w.Id && e.Status == "PROMOTED")).GuestId == a.Id ? pa : pb;
        var winnerState = (await guest.GetFromJsonAsync<RsvpDto>(Public(winner)))!;
        await Read<RsvpDto>(await guest.PutAsJsonAsync(Public(winner), new SubmitRsvp(winnerState.Version, "DECLINED", [])));
        var waitingId = (await db.WaitlistEntries.AsNoTracking().SingleAsync(e => e.WeddingId == w.Id && e.Status == "WAITING")).Id;
        var twice = await Task.WhenAll(owner.PostAsync(root + $"/waitlist/{waitingId}/promote", null), second.PostAsync(root + $"/waitlist/{waitingId}/promote", null));
        Assert.Single(twice, r => r.StatusCode == HttpStatusCode.OK);
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "WAITLIST_NOT_WAITING", Assert.Single(twice, r => r.StatusCode != HttpStatusCode.OK));
        Assert.Equal(1, await Confirmed(db, w.Id));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task FailedWaitlistWriteLeavesNoPartialRsvpHistoryOrWaitlistState() => WithApi(async (db, factory, owner) =>
    {
        // 12. Force the waitlist INSERT to fail inside the RSVP transaction.
        var w = await Wedding(owner, "rsvp03-rollback", 2);
        var a = await Family(owner, w, "A"); var c = await Single(owner, w, "C"); var d = await Single(owner, w, "D");
        var pa = await Publish(owner, w, a.Id); var pc = await Publish(owner, w, c.Id); var pd = await Publish(owner, w, d.Id);
        using var guest = factory.CreateHttpsClient();
        var main = a.Participants.Single(x => x.Type == "PRIMARY").Id;
        var first = await Created(await guest.PostAsJsonAsync(Public(pa), new SubmitRsvp(pa.Invitation.Version, "ATTENDING", [main])));
        await Created(await guest.PostAsJsonAsync(Public(pc), new SubmitRsvp(pc.Invitation.Version, "ATTENDING", Ids(c))));
        var history = await HistoryFingerprint(db); var audits = await db.AuditLogs.CountAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION inviteme.test_fail_waitlist() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'simulated waitlist failure'; END; $$;
            CREATE TRIGGER trg_test_fail_waitlist BEFORE INSERT ON inviteme.waitlist_entries FOR EACH ROW EXECUTE FUNCTION inviteme.test_fail_waitlist();
            """);
        var partial = await guest.PutAsJsonAsync(Public(pa), new SubmitRsvp(first.Version, "ATTENDING", Ids(a)));
        var whole = await guest.PostAsJsonAsync(Public(pd), new SubmitRsvp(pd.Invitation.Version, "ATTENDING", Ids(d)));
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER trg_test_fail_waitlist ON inviteme.waitlist_entries; DROP FUNCTION inviteme.test_fail_waitlist();");
        Assert.Equal(HttpStatusCode.InternalServerError, partial.StatusCode); Assert.Equal(HttpStatusCode.InternalServerError, whole.StatusCode);
        Assert.DoesNotContain("simulated", await partial.Content.ReadAsStringAsync());

        var stateA = (await guest.GetFromJsonAsync<RsvpDto>(Public(pa)))!;
        Assert.Equal((first.Version, "ATTENDING", 1, 0), (stateA.Version, stateA.Status, stateA.ConfirmedPartySize, stateA.WaitlistedPartySize));
        Assert.Equal(0, await db.Rsvps.CountAsync(x => x.InvitationId == pd.Invitation.Id));
        Assert.False(await db.GuestParticipants.AnyAsync(x => x.AttendanceStatus == "WAITLISTED"));
        Assert.Equal(0, await db.WaitlistEntries.CountAsync());
        Assert.Equal(history, await HistoryFingerprint(db)); Assert.Equal(audits, await db.AuditLogs.CountAsync());
        Assert.Equal(2, await Confirmed(db, w.Id));
    });

    private static Guid[] Ids(ImportedGuestDto g) => g.Participants.Select(x => x.Id).ToArray();
    private static Task<int> Confirmed(InviteMeDbContext db, Guid weddingId) =>
        (from p in db.GuestParticipants join g in db.WeddingGuests on p.GuestId equals g.Id
         where g.WeddingId == weddingId && p.AttendanceStatus == "ATTENDING" select p).CountAsync();
    // Seeds already-confirmed attendance directly: n ATTENDING participants on one guest.
    private static async Task Fill(InviteMeDbContext db, ImportedGuestDto g, int n)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inviteme.guest_participants SET attendance_status='ATTENDING' WHERE guest_id={g.Id}");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inviteme.guest_participants(guest_id, full_name, participant_type, attendance_status) SELECT {g.Id}, 'Filler ' || i, 'CHILD', 'ATTENDING' FROM generate_series(1, {n - 1}) AS i");
    }
    private static async Task<string> HistoryFingerprint(InviteMeDbContext db) => string.Join("\n",
        await db.Database.SqlQueryRaw<string>("SELECT row_to_json(h)::text AS \"Value\" FROM inviteme.rsvp_history h ORDER BY changed_at, id").ToListAsync());

    private static Task WithApi(Func<InviteMeDbContext, WorkspaceApiFactory, HttpClient, Task> test) => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync(); await using var factory = new WorkspaceApiFactory(db.Database.GetConnectionString()!);
        using var owner = await SignIn(factory, "owner"); await test(db, factory, owner);
    });
    private static WorkspaceInput Settings(string slug, int capacity) => new("RSVP-03 wedding", slug, capacity, "Asia/Ho_Chi_Minh",
        DateTimeOffset.UtcNow.AddDays(60), DateTimeOffset.UtcNow.AddDays(60).AddHours(3), DateTimeOffset.UtcNow.AddDays(50), "Venue", "Address");
    private static async Task<WorkspaceDto> Wedding(HttpClient c, string slug, int capacity) => await Read<WorkspaceDto>(await c.PostAsJsonAsync("/api/weddings", Settings(slug, capacity)));
    private static string Root(WorkspaceDto w) => $"/api/weddings/{w.Id}";
    private static string Public(PublicationDto p) => "/api/public/invitations/" + p.PublicPath.Split('/').Last() + "/rsvp";
    private static async Task<ImportedGuestDto> Single(HttpClient c, WorkspaceDto w, string code) =>
        await Read<ImportedGuestDto>(await c.PostAsJsonAsync(Root(w) + "/guests", new GuestInput(code, code)));
    private static async Task<ImportedGuestDto> Family(HttpClient c, WorkspaceDto w, string code) =>
        await Read<ImportedGuestDto>(await c.PostAsJsonAsync(Root(w) + "/guests", new GuestInput(code, code, Companions: [new("Spouse " + code, "SPOUSE")])));
    private static async Task<PublicationDto> Publish(HttpClient c, WorkspaceDto w, Guid guest)
    {
        var template = (await c.GetFromJsonAsync<List<TemplateDto>>("/api/invitation-templates"))!.Single(x => x.Slug == "inviteme-classic");
        var x = await Read<InvitationDto>(await c.PostAsJsonAsync(Root(w) + "/invitations", new CreateInvitation(guest, new(template.Id, "One", "Two", "Welcome"))));
        foreach (var action in new[] { "preview", "review", "approve" }) x = await Read<InvitationDto>(await c.PostAsJsonAsync(Root(w) + $"/invitations/{x.Id}/{action}", new InvitationAction(x.Version)));
        return await Read<PublicationDto>(await c.PostAsJsonAsync(Root(w) + $"/invitations/{x.Id}/publish", new InvitationAction(x.Version, DateTimeOffset.UtcNow.AddDays(90))));
    }
    private static async Task<RsvpDto> Created(HttpResponseMessage response)
    { Assert.True(response.StatusCode == HttpStatusCode.Created, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}"); return (await response.Content.ReadFromJsonAsync<RsvpDto>())!; }
    private static async Task<T> Read<T>(HttpResponseMessage response)
    { Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}"); return (await response.Content.ReadFromJsonAsync<T>())!; }
    private static async Task AssertProblem(HttpStatusCode status, string code, HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"{response.StatusCode}: {body}");
        Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }
    private static async Task<HttpClient> SignIn(WorkspaceApiFactory factory, string name, bool register = true)
    {
        var c = factory.CreateHttpsClient(); const string password = "Test-only-Pass123!";
        if (register) await Read<AccountDto>(await c.PostAsJsonAsync("/api/auth/register", new RegisterAccount(name + "@example.test", password, name)));
        var token = await Read<TokenDto>(await c.PostAsJsonAsync("/api/auth/login", new LoginAccount(name + "@example.test", password)));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken); return c;
    }
}
