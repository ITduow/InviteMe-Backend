using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Rsvps;
using InviteMe.Application.Features.Seating.Planning;
using InviteMe.Application.Features.Weddings.Setup;
using InviteMe.Application.Features.Weddings.CoHosts;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Rsvps;
using InviteMe.Domain.Seating;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace InviteMe.IntegrationTests;

/// <summary>
/// RSVP module (Guest03 + Guest04 + Couple06 rules) through the real HTTP pipeline against real PostgreSQL, on Guest
/// Planning v2 data. Guests are created with the Guest Planning import API and answer with their planning invitation token
/// (the same token as /invite/{token}). Weddings are seeded directly to keep each test focused.
/// </summary>
public sealed class RsvpApiTests
{
    private const string Password = "Test-only-Pass123!";

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task WeddingWithThirtyImportedInvitationsFlowsThroughRsvpWaitlistAndSeating() => WithApi(async (db, factory, owner, _) =>
    {
        var eventAt = DateTimeOffset.UtcNow.AddMonths(6);
        var wedding = await Read<WeddingSetupDto>(await owner.PostAsJsonAsync("/api/weddings",
            new CreateWeddingInput("Thirty guest RSVP flow", "rsvp-flow-thirty", eventAt,
                "Test hall", "Test address", 80, eventAt.AddDays(-10))));
        var w = wedding.Id;
        var emptyDraft = await owner.GetAsync($"/api/weddings/{w}/invitation-messages/draft");
        Assert.Equal(HttpStatusCode.OK, emptyDraft.StatusCode);
        Assert.Equal("null", await emptyDraft.Content.ReadAsStringAsync());
        var rows = Enumerable.Range(0, 30).Select(i => new PlanningRow(i + 2, $"QA guest {i + 1:00}",
            Honorific: i % 3 == 0 ? "Bác" : i % 3 == 1 ? "Anh" : "Bạn",
            InvitationKind: i % 3 == 0 ? "FAMILY" : i % 3 == 1 ? "GROUP" : "INDIVIDUAL",
            ExpectedPeople: i % 3 == 0 ? 4 : i % 3 == 1 ? 2 : 1,
            Side: i % 2 == 0 ? "BRIDE" : "GROOM",
            Group: new PlanningGroupInput(NewName: i % 2 == 0 ? "Họ ngoại" : "Đồng nghiệp", Confirmed: true),
            Note: i == 0 ? "Gần lối đi" : null)).ToArray();
        using var import = new HttpRequestMessage(HttpMethod.Post, $"/api/weddings/{w}/guest-planning/imports")
        { Content = JsonContent.Create(new PlanningImport(rows)) };
        import.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var imported = await Read<PlanningImportResult>(await owner.SendAsync(import));
        Assert.Equal((30, 70, 2), (imported.CreatedGuestCount, imported.CreatedPlaceCount, imported.CreatedGroups.Count));
        Assert.Equal(30, (await owner.GetFromJsonAsync<PlanningGuestPage>($"/api/weddings/{w}/guest-planning/guests?pageSize=50"))!.TotalItems);

        var family = await Seed(db, imported.Items[0].GuestId);
        var group = await Seed(db, imported.Items[1].GuestId);
        var individual = await Seed(db, imported.Items[2].GuestId);
        var lockedGuest = await Seed(db, imported.Items[3].GuestId);
        var seatingUrl = $"/api/weddings/{w}/guest-planning/seating-plan";
        var plan = (await owner.GetFromJsonAsync<PlanningSeatingDto>(seatingUrl))!;
        Assert.Equal(70, plan.Places.Count);
        plan = await Read<PlanningSeatingDto>(await owner.PatchAsJsonAsync(seatingUrl,
            new ChangePlanningSeating(plan.Version, "CREATE_TABLE", Name: "Bàn gia đình", Capacity: 4)));
        var table = Assert.Single(plan.Tables);
        var familyPlaces = plan.Places.Where(p => p.GuestId == family.GuestId).OrderBy(p => p.Position).Select(p => p.Id).ToArray();
        plan = await Read<PlanningSeatingDto>(await owner.PatchAsJsonAsync(seatingUrl,
            new ChangePlanningSeating(plan.Version, "MOVE_PLACES", table.Id, PlaceIds: familyPlaces)));
        Assert.Equal(4, plan.Assignments.Count);

        var events = new RecordingPublisher();
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        { s.RemoveAll<IPlanningChangePublisher>(); s.AddSingleton<IPlanningChangePublisher>(events); }));
        using var guest = app.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
        var initial = (await guest.GetFromJsonAsync<RsvpDto>(family.Url))!;
        Assert.Equal((4, "PENDING"), (initial.Places.Count, initial.Status));
        var answered = await Created(await guest.PostAsJsonAsync(family.Url,
            new SubmitRsvp(0, "ATTENDING", [family.Main], ["QA family 2", "QA family 3"], Note: "Gần lối đi")));
        Assert.Equal((3, "UNUSED"), (answered.ConfirmedPartySize, answered.Places[3].Status));
        Assert.Contains(events.Changes, e => e.WeddingId == w && e.Change.Type == RsvpHandler.RsvpChanged);
        Assert.Contains(events.Changes, e => e.WeddingId == w && e.Change.Type == RsvpHandler.SeatingNeedsAttention);
        plan = (await owner.GetFromJsonAsync<PlanningSeatingDto>(seatingUrl))!;
        Assert.Equal(4, plan.Assignments.Count);
        Assert.Null(plan.Places.Single(p => p.Id == familyPlaces[3]).Person);

        await Created(await guest.PostAsJsonAsync(individual.Url, new SubmitRsvp(0, "DECLINED", [])));
        var waiting = await Created(await guest.PostAsJsonAsync(group.Url,
            new SubmitRsvp(0, "ATTENDING", [group.Main], ["QA group 2", "QA extra"])));
        Assert.Equal((2, 1, RsvpCapacityOutcome.PartiallyWaitlisted),
            (waiting.ConfirmedPartySize, waiting.WaitlistedPartySize, waiting.CapacityOutcome));
        var waitlist = (await owner.GetFromJsonAsync<PagedResult<WaitlistDto>>($"/api/weddings/{w}/rsvps/waitlist"))!;
        var entry = Assert.Single(waitlist.Items);
        Assert.Equal(group.GuestId, entry.GuestId);
        var promoted = await Read<RsvpDto>(await owner.PostAsync($"/api/weddings/{w}/rsvps/waitlist/{entry.Id}/promote", null));
        Assert.Equal((3, 0), (promoted.ConfirmedPartySize, promoted.WaitlistedPartySize));
        Assert.Empty((await owner.GetFromJsonAsync<PagedResult<WaitlistDto>>($"/api/weddings/{w}/rsvps/waitlist"))!.Items);

        await Read<LockRsvpsResult>(await owner.PutAsJsonAsync($"/api/weddings/{w}/rsvps/locks", new LockRsvps(true, [lockedGuest.GuestId])));
        var queued = await guest.PostAsJsonAsync(lockedGuest.Url, new SubmitRsvp(0, "ATTENDING", [lockedGuest.Main]));
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        Assert.Equal(1, (await owner.GetFromJsonAsync<RsvpDashboardDto>($"/api/weddings/{w}/rsvps/dashboard"))!.PendingRequests);
        var request = Assert.Single((await owner.GetFromJsonAsync<PagedResult<RsvpRequestDto>>($"/api/weddings/{w}/rsvps/requests"))!.Items);
        await Read<RsvpDto>(await owner.PostAsync($"/api/weddings/{w}/rsvps/requests/{request.Id}/approve", null));

        var dashboard = (await owner.GetFromJsonAsync<RsvpDashboardDto>($"/api/weddings/{w}/rsvps/dashboard"))!;
        Assert.Equal((30, 4, 26), (dashboard.Invitations.Invited, dashboard.Invitations.Responded, dashboard.Invitations.NotResponded));
        Assert.Equal((71, 7, 73L), (dashboard.People.InvitedPlaces, dashboard.People.Confirmed, dashboard.Capacity.Remaining!.Value));
        Assert.Equal(0, dashboard.PendingRequests);
        Assert.Equal(15, dashboard.BySide.Single(x => x.Side == "BRIDE").Invited);
        Assert.Equal(15, dashboard.BySide.Single(x => x.Side == "GROOM").Invited);
        Assert.Equal(26, (await owner.GetFromJsonAsync<PagedResult<RsvpGuestRowDto>>(
            $"/api/weddings/{w}/rsvps/dashboard/guests?status=NOT_RESPONDED&pageSize=50"))!.TotalCount);
        Assert.Equal(4, await db.Set<PlanningSeatAssignment>().CountAsync(x => x.TableId == table.Id));
    });

    // ---------------- Guest03: submit / update / history ----------------

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task FirstAnswerThenChangesAppendOneImmutableHistoryRowPerConfirmedChange() => WithApi(async (db, factory, owner, ownerId) =>
    {
        var w = await Wedding(db, ownerId, "rsvp-happy", 10); var a = await Party(db, owner, w, "Anh Nam", people: 2);
        using var c = factory.CreateHttpsClient();

        var initial = (await c.GetFromJsonAsync<RsvpDto>(a.Url))!;
        Assert.Equal(("PENDING", (Guid?)null, 0, RsvpCapacityOutcome.None, 2, false), (initial.Status, initial.RsvpId, initial.Version, initial.CapacityOutcome, initial.AllowedPeople, initial.Locked));
        Assert.Equal(new[] { RsvpPlaceStatus.Pending, RsvpPlaceStatus.Pending }, initial.Places.Select(p => p.Status).ToArray());
        Assert.Equal(("Anh Nam", a.Main), (initial.RecipientName, Assert.Single(initial.Participants).Id));

        var created = await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", [a.Main], ["Chị Mai"]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); Assert.Null(created.Headers.Location);
        var first = (await created.Content.ReadFromJsonAsync<RsvpDto>())!;
        Assert.Equal(("ATTENDING", 2, 1, RsvpCapacityOutcome.Confirmed, (string?)null), (first.Status, first.ConfirmedPartySize, first.Version, first.CapacityOutcome, first.Message));
        Assert.All(first.Places, p => Assert.Equal(RsvpPlaceStatus.Used, p.Status));
        var mai = first.Participants.Single(p => p.FullName == "Chị Mai");
        Assert.Equal(("COMPANION", "ATTENDING"), (mai.Role, mai.AttendanceStatus));
        var h1 = Assert.Single(await History(db));
        Assert.Null(h1.OldStatus); Assert.Equal(("RSVP_SUBMITTED", "WEDDING_GUEST", a.GuestId), (h1.Reason, h1.ActorType, h1.ActorGuestId!.Value));
        Assert.DoesNotContain(a.Token, h1.NewSnapshot); Assert.DoesNotContain("Mai", h1.NewSnapshot);

        // One source of truth: the Guest Planning guest detail shows the RSVP answer of each person.
        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/weddings/{w}/guest-planning/guests/{a.GuestId}");
        Assert.All(detail.GetProperty("places").EnumerateArray(), p =>
            Assert.Equal("ATTENDING", p.GetProperty("person").GetProperty("attendanceStatus").GetString()));
        var frozen = await HistoryFingerprint(db);

        await AssertProblem(HttpStatusCode.Conflict, "RSVP_ALREADY_EXISTS", await c.PostAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "DECLINED", [])));
        var reduced = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "ATTENDING", [a.Main])));
        Assert.Equal((1, "DECLINED"), (reduced.ConfirmedPartySize, reduced.Participants.Single(p => p.Id == mai.Id).AttendanceStatus));
        Assert.Equal(new[] { RsvpPlaceStatus.Used, RsvpPlaceStatus.Unused }, reduced.Places.Select(p => p.Status).ToArray());
        Assert.Equal(first.SubmittedAt, reduced.SubmittedAt);
        var same = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(reduced.Version, "ATTENDING", [a.Main])));
        Assert.Equal(reduced.Version, same.Version); // no-op
        await AssertProblem(HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT", await c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "DECLINED", [])));
        var declined = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(same.Version, "DECLINED", [])));
        Assert.Equal(("DECLINED", 0), (declined.Status, declined.ConfirmedPartySize));
        Assert.All(declined.Places, p => Assert.Equal(RsvpPlaceStatus.Unused, p.Status));
        var all = await History(db);
        Assert.Equal(3, all.Count); Assert.Equal(("ATTENDING", 1, "DECLINED", 0), (all[2].OldStatus, all[2].OldConfirmedPartySize!.Value, all[2].NewStatus, all[2].NewConfirmedPartySize));
        Assert.StartsWith(frozen, await HistoryFingerprint(db)); // earlier rows unchanged

        // Host route: create/replace on the guest's behalf by planning guest id, recorded with a USER actor.
        await Read<RsvpDto>(await owner.PutAsJsonAsync($"/api/weddings/{w}/rsvps/{a.GuestId}", new SubmitRsvp(declined.Version, "ATTENDING", [a.Main, mai.Id])));
        var host = (await History(db)).Last();
        Assert.Equal(("USER", ownerId), (host.ActorType, host.ChangedBy!.Value));
        Assert.Equal(2, (await owner.GetFromJsonAsync<RsvpDto>($"/api/weddings/{w}/rsvps/{a.GuestId}"))!.ConfirmedPartySize);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task PlacesLeftEmptyBecomeUnusedWithoutFakePeople() => WithApi(async (db, factory, owner, ownerId) =>
    {
        // RSVP DOC: family of 4, three come, the fourth place stays empty → 3 people, 4 places, place 4 UNUSED.
        var w = await Wedding(db, ownerId, "rsvp-places", 10); var an = await Party(db, owner, w, "Nguyễn Văn An", people: 4);
        using var c = factory.CreateHttpsClient();
        var first = await Created(await c.PostAsJsonAsync(an.Url, new SubmitRsvp(0, "ATTENDING", [an.Main], ["Trần Thị Hoa", "Nguyễn Văn Bình"])));
        Assert.Equal((3, 3, 4), (first.ConfirmedPartySize, first.Participants.Count, first.Places.Count));
        Assert.Equal(new[] { "USED", "USED", "USED", "UNUSED" }, first.Places.Select(p => p.Status).ToArray());
        Assert.Null(first.Places[3].ParticipantId);
        Assert.Equal(3, await db.Set<PlanningParticipant>().CountAsync(x => x.GuestId == an.GuestId));
        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/weddings/{w}/guest-planning/guests/{an.GuestId}");
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("places")[3].GetProperty("person").ValueKind); // nothing written on Guest Planning data

        // Later the fourth person comes after all: the empty place is filled, no new place is created.
        var later = await Read<RsvpDto>(await c.PutAsJsonAsync(an.Url, new SubmitRsvp(first.Version, "ATTENDING", first.Participants.Select(p => p.Id).ToArray(), ["Nguyễn Văn Dũng"])));
        Assert.Equal((4, 4), (later.ConfirmedPartySize, later.Places.Count));
        Assert.All(later.Places, p => Assert.Equal(RsvpPlaceStatus.Used, p.Status));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task PlusOneLimitAllowsCompanionsAndPeopleBeyondTheInvitationWaitForTheHosts() => WithApi(async (db, factory, owner, ownerId) =>
    {
        // Couple06: global plus-one limit at wedding setup (1 = two people per invitation); a bigger imported family overrides it;
        // people beyond what the invitation allows go to the waitlist even when the wedding still has room.
        var w = await Wedding(db, ownerId, "rsvp-plus-one", 100);
        var settings = await Read<RsvpSettingsDto>(await owner.PutAsJsonAsync($"/api/weddings/{w}/rsvp-settings", new SaveRsvpSettings(0, 1)));
        Assert.Equal((1, 1), (settings.PlusOneLimit, settings.Version));
        await AssertProblem(HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT", await owner.PutAsJsonAsync($"/api/weddings/{w}/rsvp-settings", new SaveRsvpSettings(0, 2)));
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/weddings/{w}/rsvp-settings", new SaveRsvpSettings(1, 21))).StatusCode);
        var single = await Party(db, owner, w, "Lan"); var other = await Party(db, owner, w, "Hùng"); var family = await Party(db, owner, w, "Bác An", people: 4);
        using var c = factory.CreateHttpsClient();

        Assert.Equal(2, (await c.GetFromJsonAsync<RsvpDto>(single.Url))!.AllowedPeople);
        Assert.Equal(4, (await c.GetFromJsonAsync<RsvpDto>(family.Url))!.AllowedPeople); // the family overrides the global limit
        var lan = await Created(await c.PostAsJsonAsync(single.Url, new SubmitRsvp(0, "ATTENDING", [single.Main], ["Bạn của Lan"])));
        Assert.Equal((2, 2, RsvpCapacityOutcome.Confirmed), (lan.ConfirmedPartySize, lan.Places.Count, lan.CapacityOutcome)); // a new place within the limit

        var hung = await Created(await c.PostAsJsonAsync(other.Url, new SubmitRsvp(0, "ATTENDING", [other.Main], ["Vợ Hùng", "Con Hùng"])));
        Assert.Equal(("ATTENDING", 2, 1, RsvpCapacityOutcome.PartiallyWaitlisted), (hung.Status, hung.ConfirmedPartySize, hung.WaitlistedPartySize, hung.CapacityOutcome));
        Assert.NotNull(hung.Message);
        var waitingPerson = hung.Participants.Single(p => p.AttendanceStatus == "WAITLISTED");
        Assert.Equal("Con Hùng", waitingPerson.FullName);
        Assert.Equal(2, hung.Places.Count); // the waiting person has no place yet
        var entry = Assert.Single((await owner.GetFromJsonAsync<PagedResult<WaitlistDto>>($"/api/weddings/{w}/rsvps/waitlist"))!.Items);
        Assert.Equal((other.GuestId, 1, WaitlistKind.AdditionalMember), (entry.GuestId, entry.RequestedSlots, entry.Kind));

        var bigFamily = await Created(await c.PostAsJsonAsync(family.Url, new SubmitRsvp(0, "ATTENDING", [family.Main], ["B", "C", "D", "E"])));
        Assert.Equal((4, 1), (bigFamily.ConfirmedPartySize, bigFamily.WaitlistedPartySize)); // a 5th person waits

        // Hosts accept Hùng's child (gets a place) and turn down the family's fifth person.
        var promoted = await Read<RsvpDto>(await owner.PostAsync($"/api/weddings/{w}/rsvps/waitlist/{entry.Id}/promote", null));
        Assert.Equal((3, 0, 3), (promoted.ConfirmedPartySize, promoted.WaitlistedPartySize, promoted.Places.Count));
        Assert.Equal(waitingPerson.Id, promoted.Places[2].ParticipantId);
        var familyEntry = (await owner.GetFromJsonAsync<PagedResult<WaitlistDto>>($"/api/weddings/{w}/rsvps/waitlist"))!.Items.Single();
        var turnedDown = await Read<RsvpDto>(await owner.PostAsync($"/api/weddings/{w}/rsvps/waitlist/{familyEntry.Id}/decline", null));
        Assert.Equal(("ATTENDING", 4, 0, RsvpCapacityOutcome.Confirmed), (turnedDown.Status, turnedDown.ConfirmedPartySize, turnedDown.WaitlistedPartySize, turnedDown.CapacityOutcome));
        Assert.Equal("CANCELLED", (await db.Set<PlanningWaitlistEntry>().AsNoTracking().SingleAsync(x => x.Id == familyEntry.Id)).Status);
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "WAITLIST_NOT_WAITING", await owner.PostAsync($"/api/weddings/{w}/rsvps/waitlist/{familyEntry.Id}/promote", null));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task WalkInsAddedAtReceptionUseTheSameCapacity() => WithApi(async (db, factory, owner, ownerId) =>
    {
        // Cohost04 walk-ins are planning people with ATTENDING, so RSVP counts them (the old walk_ins table is not used).
        var w = await Wedding(db, ownerId, "rsvp-walkin-capacity", 3);
        var a = await Party(db, owner, w, "A", people: 2);
        using var walkIn = new HttpRequestMessage(HttpMethod.Post, $"/api/weddings/{w}/guest-planning/walk-ins")
        { Content = JsonContent.Create(new InviteMe.Application.Features.Guests.Reception.AddWalkIn(null, "Khách vãng lai", 2)) };
        walkIn.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.True((await owner.SendAsync(walkIn)).IsSuccessStatusCode);
        using var c = factory.CreateHttpsClient();
        var full = await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", [a.Main], ["A2"]));
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "INSUFFICIENT_SLOTS", full);
        Assert.Contains("slot còn lại 1 người", JsonDocument.Parse(await full.Content.ReadAsStringAsync()).RootElement.GetProperty("detail").GetString());
        var dashboard = (await owner.GetFromJsonAsync<RsvpDashboardDto>($"/api/weddings/{w}/rsvps/dashboard"))!;
        Assert.Equal((3, 2L, 1L), (dashboard.Capacity.Max, dashboard.Capacity.Occupied, dashboard.Capacity.Remaining));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task MorePeopleThanTheWeddingHasLeftIsRefusedWithTheRemainingSlots() => WithApi(async (db, factory, owner, ownerId) =>
    {
        // Couple06: INSUFFICIENT_SLOTS ("Số lượng người đi vượt quá, slot còn lại {n} người"); nothing is saved.
        var w = await Wedding(db, ownerId, "rsvp-slots", 3);
        var a = await Party(db, owner, w, "A", people: 2); var b = await Party(db, owner, w, "B", people: 2);
        using var c = factory.CreateHttpsClient();
        var ra = await Created(await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", [a.Main], ["A2"])));
        var full = await c.PostAsJsonAsync(b.Url, new SubmitRsvp(0, "ATTENDING", [b.Main], ["B2"]));
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "INSUFFICIENT_SLOTS", full);
        Assert.Contains("slot còn lại 1 người", JsonDocument.Parse(await full.Content.ReadAsStringAsync()).RootElement.GetProperty("detail").GetString());
        Assert.Equal(0, await db.Set<PlanningRsvp>().CountAsync(x => x.GuestId == b.GuestId));
        Assert.Equal(1, await db.Set<PlanningParticipant>().CountAsync(x => x.GuestId == b.GuestId));
        var rb = await Created(await c.PostAsJsonAsync(b.Url, new SubmitRsvp(0, "ATTENDING", [b.Main])));
        Assert.Equal(1, rb.ConfirmedPartySize);
        Assert.Equal(3, await Confirmed(db, w));

        // A confirmed party asking for one more when full: refused, and what it had stays.
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "INSUFFICIENT_SLOTS", await c.PutAsJsonAsync(b.Url, new SubmitRsvp(rb.Version, "ATTENDING", [b.Main], ["B2"])));
        Assert.Equal(1, (await c.GetFromJsonAsync<RsvpDto>(b.Url))!.ConfirmedPartySize);
        // Once A comes alone, the freed slot lets B bring B2.
        var reduced = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(ra.Version, "ATTENDING", [a.Main])));
        Assert.Equal(1, reduced.ConfirmedPartySize);
        var grown = await Read<RsvpDto>(await c.PutAsJsonAsync(b.Url, new SubmitRsvp(rb.Version, "ATTENDING", [b.Main], ["B2"])));
        Assert.Equal(2, grown.ConfirmedPartySize);
        Assert.Equal(3, await Confirmed(db, w));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task AfterTheDeadlineOrALockGuestChangesWaitForTheHosts() => WithApi(async (db, factory, owner, ownerId) =>
    {
        // Couple06 + committee Q4: after the deadline (or when the hosts lock a guest / a group) the guest can only send a
        // change request; numbers change only when a host approves it.
        var w = await Wedding(db, ownerId, "rsvp-lock", 10);
        var a = await Party(db, owner, w, "A", people: 2);
        using var c = factory.CreateHttpsClient();
        var first = await Created(await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", [a.Main], ["A2"])));
        var settings = WeddingSettings.Create(w); settings.Configure("Asia/Ho_Chi_Minh", "PRIVATE", DateTimeOffset.UtcNow.AddMinutes(-1), 2);
        db.WeddingSettings.Add(settings); await db.SaveChangesAsync();

        var late = await c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "ATTENDING", [a.Main]));
        Assert.Equal(HttpStatusCode.Accepted, late.StatusCode);
        var queued = (await late.Content.ReadFromJsonAsync<RsvpDto>())!;
        Assert.Equal((first.Version, 2, true, RsvpCapacityOutcome.LockedMessage), (queued.Version, queued.ConfirmedPartySize, queued.Locked, queued.Message));
        Assert.Equal(("DEADLINE_PASSED", "PENDING"), (queued.PendingRequest!.Reason, queued.PendingRequest.Status));
        var newer = (await (await c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "DECLINED", []))).Content.ReadFromJsonAsync<RsvpDto>())!;
        Assert.Equal("DECLINED", newer.PendingRequest!.Change.Decision); // the latest change replaces the older request
        Assert.Equal(1, await db.Set<PlanningRsvpHistory>().CountAsync());

        var requests = (await owner.GetFromJsonAsync<PagedResult<RsvpRequestDto>>($"/api/weddings/{w}/rsvps/requests"))!;
        var request = Assert.Single(requests.Items);
        Assert.Equal(("A", "DECLINED"), (request.RecipientName, request.Change.Decision));
        Assert.Single((await owner.GetFromJsonAsync<PagedResult<RsvpRequestDto>>($"/api/weddings/{w}/rsvps/requests?status=SUPERSEDED"))!.Items);
        var applied = await Read<RsvpDto>(await owner.PostAsync($"/api/weddings/{w}/rsvps/requests/{request.Id}/approve", null));
        Assert.Equal(("DECLINED", 0, (RsvpRequestDto?)null), (applied.Status, applied.ConfirmedPartySize, applied.PendingRequest));
        Assert.Equal(("RSVP_CHANGE_APPROVED", "USER"), ((await History(db)).Last().Reason, (await History(db)).Last().ActorType));
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "REQUEST_NOT_PENDING", await owner.PostAsync($"/api/weddings/{w}/rsvps/requests/{request.Id}/approve", null));

        // Rejecting keeps the RSVP as it is.
        var again = (await (await c.PutAsJsonAsync(a.Url, new SubmitRsvp(applied.Version, "ATTENDING", [a.Main]))).Content.ReadFromJsonAsync<RsvpDto>())!;
        var rejected = await Read<RsvpDto>(await owner.PostAsync($"/api/weddings/{w}/rsvps/requests/{again.PendingRequest!.Id}/reject", null));
        Assert.Equal(("DECLINED", (RsvpRequestDto?)null), (rejected.Status, rejected.PendingRequest));

        // Hosts lock a whole group before the deadline: the group's guests can only send requests; hosts still edit directly.
        var open = await Wedding(db, ownerId, "rsvp-lock-group", 10);
        var request2 = new HttpRequestMessage(HttpMethod.Post, $"/api/weddings/{open}/guest-planning/imports")
        {
            Content = JsonContent.Create(new PlanningImport([
                new PlanningRow(2, "Nhà gái 1", Group: new PlanningGroupInput(NewName: "Nhà gái", Confirmed: true)),
                new PlanningRow(3, "Nhà gái 2", Group: new PlanningGroupInput(NewName: "Nhà gái", Confirmed: true))]))
        };
        request2.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var imported = await Read<PlanningImportResult>(await owner.SendAsync(request2));
        var groupId = Assert.Single(imported.CreatedGroups).Id;
        var locked = await Read<LockRsvpsResult>(await owner.PutAsJsonAsync($"/api/weddings/{open}/rsvps/locks", new LockRsvps(true, GroupId: groupId)));
        Assert.Equal(2, locked.Affected);
        var g1 = await Seed(db, imported.Items[0].GuestId);
        Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsJsonAsync(g1.Url, new SubmitRsvp(0, "ATTENDING", [g1.Main]))).StatusCode);
        Assert.True((await c.GetFromJsonAsync<RsvpDto>(g1.Url))!.Locked);
        // A host entering the answer directly closes the guest's waiting request: it no longer counts as pending.
        var direct = await Read<RsvpDto>(await owner.PutAsJsonAsync($"/api/weddings/{open}/rsvps/{g1.GuestId}", new SubmitRsvp(0, "ATTENDING", [g1.Main], Source: RsvpSource.Phone)));
        Assert.Null(direct.PendingRequest);
        Assert.Empty((await owner.GetFromJsonAsync<PagedResult<RsvpRequestDto>>($"/api/weddings/{open}/rsvps/requests"))!.Items);
        var closed = Assert.Single((await owner.GetFromJsonAsync<PagedResult<RsvpRequestDto>>($"/api/weddings/{open}/rsvps/requests?status=SUPERSEDED"))!.Items);
        Assert.Equal(g1.GuestId, closed.GuestId);
        Assert.Equal(2, (await Read<LockRsvpsResult>(await owner.PutAsJsonAsync($"/api/weddings/{open}/rsvps/locks", new LockRsvps(false, GroupId: groupId)))).Affected);
        var g2 = await Seed(db, imported.Items[1].GuestId);
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync(g2.Url, new SubmitRsvp(0, "ATTENDING", [g2.Main]))).StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task InvalidRequestsWriteNothing() => WithApi(async (db, factory, owner, ownerId) =>
    {
        var w = await Wedding(db, ownerId, "rsvp-invalid", 10); var a = await Party(db, owner, w, "A", people: 2);
        var other = await Party(db, owner, w, "B");
        using var c = factory.CreateHttpsClient();
        foreach (var body in new object[]
                 {
                     new SubmitRsvp(0, "DECLINED", [a.Main]), new SubmitRsvp(0, "ATTENDING", []), new SubmitRsvp(0, "MAYBE", [a.Main]),
                     new SubmitRsvp(-1, "ATTENDING", [a.Main]), new SubmitRsvp(0, "ATTENDING", [a.Main], Note: new string('x', 501)),
                     new SubmitRsvp(0, "ATTENDING", [a.Main], DietaryNotes: [new(a.Main, new string('x', 201))]),
                     new { expectedVersion = 0, decision = "ATTENDING", participantIds = new[] { a.Main }, unknown = 1 }
                 })
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync(a.Url, body)).StatusCode);
        await AssertProblem(HttpStatusCode.NotFound, "INVITATION_UNAVAILABLE", await c.GetAsync("/api/rsvp/" + new string('a', 64)));
        await AssertProblem(HttpStatusCode.NotFound, "INVITATION_UNAVAILABLE", await c.GetAsync("/api/rsvp/not-a-token"));
        await AssertProblem(HttpStatusCode.NotFound, "RSVP_NOT_FOUND", await c.PutAsJsonAsync(a.Url, new SubmitRsvp(0, "DECLINED", [])));
        await AssertProblem(HttpStatusCode.NotFound, "PARTICIPANT_NOT_FOUND", await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", [other.Main])));
        await AssertProblem(HttpStatusCode.NotFound, "PARTICIPANT_NOT_FOUND", await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", [a.Main], DietaryNotes: [new(other.Main, "Chay")])));
        await AssertProblem(HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT", await c.PostAsJsonAsync(a.Url, new SubmitRsvp(1, "ATTENDING", [a.Main])));
        Assert.Equal(0, await db.Set<PlanningRsvp>().CountAsync()); Assert.Equal(0, await db.Set<PlanningRsvpHistory>().CountAsync());
        Assert.Equal(1, await db.Set<PlanningParticipant>().CountAsync(x => x.GuestId == a.GuestId));

        // A closed wedding gives the same 404 as an unknown token (same rule as GET /api/guest-invitations/{token}).
        var closed = await Wedding(db, ownerId, "rsvp-closed", 10); var z = await Party(db, owner, closed, "Z");
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(z.Url)).StatusCode);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inviteme.weddings SET status='CANCELLED' WHERE id={closed}");
        await AssertProblem(HttpStatusCode.NotFound, "INVITATION_UNAVAILABLE", await c.GetAsync(z.Url));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task NotesAndDietaryNotesAreSavedWithoutRsvpHistory() => WithApi(async (db, factory, owner, ownerId) =>
    {
        var w = await Wedding(db, ownerId, "rsvp-notes", 10); var a = await Party(db, owner, w, "A", people: 2);
        using var c = factory.CreateHttpsClient();
        var first = await Created(await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", [a.Main], ["Mai"],
            Note: "  Xin bàn gần sân khấu  ", DietaryNotes: [new(a.Main, "Ăn chay")])));
        Assert.Equal("Xin bàn gần sân khấu", first.Note);
        Assert.Equal("Ăn chay", first.Participants.Single(p => p.Id == a.Main).DietaryNote);
        var mai = first.Participants.Single(p => p.FullName == "Mai").Id;
        Assert.Null(first.Participants.Single(p => p.Id == mai).DietaryNote);
        Assert.Equal(1, await db.Set<PlanningRsvpHistory>().CountAsync());
        Assert.DoesNotContain("chay", (await History(db)).Single().NewSnapshot, StringComparison.OrdinalIgnoreCase);

        // Only notes change → saved, version advances, no RSVP history row.
        var changed = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "ATTENDING", [a.Main, mai], Note: "Đi xe riêng", DietaryNotes: [new(mai, "Không cay")])));
        Assert.Equal(("Đi xe riêng", first.Version + 1, "Không cay"), (changed.Note, changed.Version, changed.Participants.Single(p => p.Id == mai).DietaryNote));
        Assert.Equal("Ăn chay", changed.Participants.Single(p => p.Id == a.Main).DietaryNote); // omitted = unchanged
        Assert.Equal(1, await db.Set<PlanningRsvpHistory>().CountAsync());
        var audit = await db.AuditLogs.AsNoTracking().SingleAsync(x => x.Action == "RSVP_NOTE_UPDATED");
        Assert.Equal(a.GuestId, JsonDocument.Parse(audit.Metadata!).RootElement.GetProperty("guestId").GetGuid());

        // Empty note / blank dietary notes clear them.
        var cleared = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(changed.Version, "ATTENDING", [a.Main, mai], Note: "", DietaryNotes: [new(a.Main, null), new(mai, " ")])));
        Assert.Null(cleared.Note); Assert.All(cleared.Participants, p => Assert.Null(p.DietaryNote));
        Assert.Equal(0, await db.Set<PlanningDietaryNote>().CountAsync());
        var again = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(cleared.Version, "ATTENDING", [a.Main, mai], Note: "")));
        Assert.Equal(cleared.Version, again.Version); // nothing to change → no-op
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task HistoryIsAppendOnlyAndFailedWritesRollBack() => WithApi(async (db, factory, owner, ownerId) =>
    {
        var w = await Wedding(db, ownerId, "rsvp-guards", 10); var a = await Party(db, owner, w, "A"); var d = await Party(db, owner, w, "D");
        using var c = factory.CreateHttpsClient();
        var first = await Created(await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", [a.Main])));
        var frozen = await HistoryFingerprint(db); var audits = await db.AuditLogs.CountAsync();
        foreach (var sql in new[] { "UPDATE inviteme.planning_rsvp_history SET reason = 'x'", "DELETE FROM inviteme.planning_rsvp_history", "TRUNCATE inviteme.planning_rsvp_history" })
            Assert.Equal(PostgresErrorCodes.RestrictViolation, (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql))).SqlState);

        // Failing history / waitlist inserts: RSVP, people, places, notes, waitlist, history and audit all roll back.
        foreach (var (table, call) in new (string, Func<Task<HttpResponseMessage>>)[]
                 {
                     ("planning_rsvp_history", () => c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "DECLINED", [], Note: "x"))),
                     ("planning_rsvp_waitlist", () => c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "ATTENDING", [a.Main], ["Extra"]))),
                     ("planning_rsvp_waitlist", () => c.PostAsJsonAsync(d.Url, new SubmitRsvp(0, "ATTENDING", [d.Main], ["Extra"])))
                 })
        {
            // Table names are fixed test constants (not user input).
            var install = "CREATE FUNCTION inviteme.test_fail() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'simulated failure'; END; $$; " +
                "CREATE TRIGGER trg_test_fail BEFORE INSERT ON inviteme." + table + " FOR EACH ROW EXECUTE FUNCTION inviteme.test_fail();";
            var remove = "DROP TRIGGER trg_test_fail ON inviteme." + table + "; DROP FUNCTION inviteme.test_fail();";
            await db.Database.ExecuteSqlRawAsync(install);
            var failed = await call();
            await db.Database.ExecuteSqlRawAsync(remove);
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
            Assert.DoesNotContain("simulated", await failed.Content.ReadAsStringAsync());
        }
        var state = (await c.GetFromJsonAsync<RsvpDto>(a.Url))!;
        Assert.Equal((first.Version, "ATTENDING", 1, 0, (string?)null, 1), (state.Version, state.Status, state.ConfirmedPartySize, state.WaitlistedPartySize, state.Note, state.Participants.Count));
        Assert.Equal(0, await db.Set<PlanningRsvp>().CountAsync(x => x.GuestId == d.GuestId));
        Assert.Equal(("PENDING", 1), ((await db.Set<PlanningParticipant>().AsNoTracking().SingleAsync(x => x.GuestId == d.GuestId)).AttendanceStatus,
            await db.Set<PlanningPlace>().CountAsync(x => x.GuestId == d.GuestId)));
        Assert.Equal(0, await db.Set<PlanningWaitlistEntry>().CountAsync());
        Assert.Equal(frozen, await HistoryFingerprint(db)); Assert.Equal(audits, await db.AuditLogs.CountAsync());
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task SeatedPlacesThatStopBeingUsedKeepTheSeatAndAreFlaggedForTheHost() => WithApi(async (db, factory, owner, ownerId) =>
    {
        // Couple06 / committee Q8: family of 4 seated at table T1, only 3 come → seat 4 stays (red for the hosts), never removed.
        var w = await Wedding(db, ownerId, "rsvp-seat-flag", 10); var an = await Party(db, owner, w, "Bác An", people: 4);
        var events = new RecordingPublisher();
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IPlanningChangePublisher>(); s.AddSingleton<IPlanningChangePublisher>(events);
        }));
        using var c = app.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
        var table = new PlanningTable { Id = Guid.NewGuid(), WeddingId = w, Name = "Bàn 1", Capacity = 10 }; db.Add(table);
        var places = await db.Set<PlanningPlace>().AsNoTracking().Where(x => x.GuestId == an.GuestId).OrderBy(x => x.Position).ToListAsync();
        db.AddRange(places.Select((p, i) => new PlanningSeatAssignment { PlaceId = p.Id, TableId = table.Id, SeatNumber = i + 1 }));
        await db.SaveChangesAsync();

        var first = await Created(await c.PostAsJsonAsync(an.Url, new SubmitRsvp(0, "ATTENDING", [an.Main], ["Hoa", "Bình"])));
        Assert.Contains(events.Changes, x => (x.WeddingId, x.Change.Type, x.Change.EntityId, x.Change.Version) == (w, RsvpHandler.RsvpChanged, an.GuestId, (int?)1));
        Assert.Contains(events.Changes, x => x.Change.Type == RsvpHandler.SeatingNeedsAttention && x.WeddingId == w);
        Assert.Equal(4, await db.Set<PlanningSeatAssignment>().CountAsync(x => x.TableId == table.Id)); // seat 4 kept
        var flag = await db.AuditLogs.AsNoTracking().SingleAsync(x => x.Action == "SEAT_NEEDS_ATTENTION");
        Assert.Equal(("planning_seat_assignments", places[3].Id), (flag.EntityType, flag.EntityId));

        // Bình drops out later: his seat is flagged too, still not removed.
        var binh = first.Participants.Single(p => p.FullName == "Bình").Id;
        await Read<RsvpDto>(await c.PutAsJsonAsync(an.Url, new SubmitRsvp(first.Version, "ATTENDING", [an.Main, first.Participants.Single(p => p.FullName == "Hoa").Id])));
        var flags = await db.AuditLogs.AsNoTracking().Where(x => x.Action == "SEAT_NEEDS_ATTENTION").OrderBy(x => x.CreatedAt).ToListAsync();
        Assert.Equal(2, flags.Count);
        using (var meta = JsonDocument.Parse(flags[1].Metadata!))
        {
            Assert.Equal(binh, meta.RootElement.GetProperty("participantId").GetGuid());
            Assert.Equal(RsvpPlaceStatus.Unused, meta.RootElement.GetProperty("placeStatus").GetString());
        }
        Assert.Equal(4, await db.Set<PlanningSeatAssignment>().CountAsync(x => x.TableId == table.Id));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task AnswerSourceIsRecordedForHostEntriesAndRejectedOnTheGuestLink() => WithApi(async (db, factory, owner, ownerId) =>
    {
        // Committee Q6: hosts enter answers received by phone/chat/in person; the source is kept in the audit log.
        var w = await Wedding(db, ownerId, "rsvp-source", 10); var a = await Party(db, owner, w, "A"); var b = await Party(db, owner, w, "B");
        using var c = factory.CreateHttpsClient();
        await AssertProblem(HttpStatusCode.BadRequest, "VALIDATION_FAILED", await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", [a.Main], Source: RsvpSource.Phone)));
        await Created(await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", [a.Main])));
        var host = $"/api/weddings/{w}/rsvps/{b.GuestId}";
        await AssertProblem(HttpStatusCode.BadRequest, "VALIDATION_FAILED", await owner.PutAsJsonAsync(host, new SubmitRsvp(0, "ATTENDING", [b.Main], Source: "FAX")));
        await Read<RsvpDto>(await owner.PutAsJsonAsync(host, new SubmitRsvp(0, "ATTENDING", [b.Main], Source: RsvpSource.Phone)));
        var sources = await db.AuditLogs.AsNoTracking().Where(x => x.EntityType == "planning_rsvps").OrderBy(x => x.CreatedAt)
            .Select(x => new { x.ActorType, x.Metadata }).ToListAsync();
        Assert.Equal(2, sources.Count);
        var guestLink = JsonDocument.Parse(sources[0].Metadata!).RootElement;
        Assert.Equal(("SYSTEM", RsvpSource.GuestLink, a.GuestId), (sources[0].ActorType, guestLink.GetProperty("source").GetString(), guestLink.GetProperty("guestId").GetGuid()));
        Assert.Equal(("USER", RsvpSource.Phone), (sources[1].ActorType, JsonDocument.Parse(sources[1].Metadata!).RootElement.GetProperty("source").GetString()));
    });

    // ---------------- Guest04: capacity under concurrency ----------------

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ConcurrentRequestsForTheLastPlacesNeverOverbook() => WithApi(async (db, factory, owner, ownerId) =>
    {
        // architecture.md acceptance: capacity 150 with 148 attending, two independent +2 requests at once.
        var w = await Wedding(db, ownerId, "rsvp-race", 150);
        var filler = await Party(db, owner, w, "FILL");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inviteme.planning_participants SET \"AttendanceStatus\"='ATTENDING' WHERE \"GuestId\"={filler.GuestId}");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inviteme.planning_participants(\"Id\", \"GuestId\", \"FullName\", \"Role\", \"AttendanceStatus\") SELECT gen_random_uuid(), {filler.GuestId}, 'Filler ' || i, 'COMPANION', 'ATTENDING' FROM generate_series(1, 147) AS i");
        var a = await Party(db, owner, w, "A", people: 2); var b = await Party(db, owner, w, "B", people: 2);
        using var ca = factory.CreateHttpsClient(); using var cb = factory.CreateHttpsClient();
        var races = await Task.WhenAll(ca.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", [a.Main], ["A2"])),
            cb.PostAsJsonAsync(b.Url, new SubmitRsvp(0, "ATTENDING", [b.Main], ["B2"])));
        Assert.Single(races, r => r.StatusCode == HttpStatusCode.Created);
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "INSUFFICIENT_SLOTS", Assert.Single(races, r => r.StatusCode != HttpStatusCode.Created));
        Assert.Equal(150, await Confirmed(db, w));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ConcurrentPromotionsCannotOverfillCapacity() => WithApi(async (db, factory, owner, ownerId) =>
    {
        // Plus-one limit 0: each extra companion waits; capacity 3 leaves room for only one of them.
        var w = await Wedding(db, ownerId, "rsvp-promote", 3);
        var a = await Party(db, owner, w, "A"); var b = await Party(db, owner, w, "B");
        using var c = factory.CreateHttpsClient();
        await Created(await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", [a.Main], ["A2"])));
        var rb = await Created(await c.PostAsJsonAsync(b.Url, new SubmitRsvp(0, "ATTENDING", [b.Main], ["B2"])));
        var entries = (await owner.GetFromJsonAsync<PagedResult<WaitlistDto>>($"/api/weddings/{w}/rsvps/waitlist"))!.Items;
        Assert.Equal(new[] { a.GuestId, b.GuestId }, entries.Select(e => e.GuestId).ToArray()); // request order

        using var second = await SignIn(factory, "owner", register: false);
        var races = await Task.WhenAll(owner.PostAsync($"/api/weddings/{w}/rsvps/waitlist/{entries[0].Id}/promote", null),
            second.PostAsync($"/api/weddings/{w}/rsvps/waitlist/{entries[1].Id}/promote", null));
        Assert.Single(races, r => r.StatusCode == HttpStatusCode.OK);
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "INSUFFICIENT_SLOTS", Assert.Single(races, r => r.StatusCode != HttpStatusCode.OK));
        Assert.Equal(3, await Confirmed(db, w));

        var winner = (await db.Set<PlanningWaitlistEntry>().AsNoTracking().SingleAsync(e => e.Status == "PROMOTED")).GuestId == a.GuestId ? a : b;
        var ws = (await c.GetFromJsonAsync<RsvpDto>(winner.Url))!;
        await Read<RsvpDto>(await c.PutAsJsonAsync(winner.Url, new SubmitRsvp(ws.Version, "DECLINED", [])));
        var waitingId = (await db.Set<PlanningWaitlistEntry>().AsNoTracking().SingleAsync(e => e.Status == "WAITING")).Id;
        var twice = await Task.WhenAll(owner.PostAsync($"/api/weddings/{w}/rsvps/waitlist/{waitingId}/promote", null),
            second.PostAsync($"/api/weddings/{w}/rsvps/waitlist/{waitingId}/promote", null));
        Assert.Single(twice, r => r.StatusCode == HttpStatusCode.OK);
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "WAITLIST_NOT_WAITING", Assert.Single(twice, r => r.StatusCode != HttpStatusCode.OK));
        Assert.Equal(2, await Confirmed(db, w));
        Assert.Equal(2, rb.Participants.Count);
    });

    // ---------------- Couple06: dashboard ----------------

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task DashboardCountsFiltersListsNonRespondersAndExports() => WithApi(async (db, factory, owner, ownerId) =>
    {
        var w = await Wedding(db, ownerId, "rsvp-dashboard", 10);
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/weddings/{w}/guest-planning/imports")
        {
            Content = JsonContent.Create(new PlanningImport([
                new PlanningRow(2, "Anh Nam", ExpectedPeople: 2, Side: "GROOM"),
                new PlanningRow(3, "Chị Lan", Side: "BRIDE"),
                new PlanningRow(4, "Cô Hoa", Side: "BRIDE", Phone: "0901234567"),
                new PlanningRow(5, "Bạn Hùng", Side: "GROOM")]))
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var items = (await Read<PlanningImportResult>(await owner.SendAsync(request))).Items;
        var (nam, lan, hoa, hung) = (await Seed(db, items[0].GuestId), await Seed(db, items[1].GuestId), await Seed(db, items[2].GuestId), await Seed(db, items[3].GuestId));
        using var c = factory.CreateHttpsClient();
        await Created(await c.PostAsJsonAsync(nam.Url, new SubmitRsvp(0, "ATTENDING", [nam.Main], ["Chị Mai"], Note: "Gần sân khấu", DietaryNotes: [new(nam.Main, "Ăn chay")])));
        await Created(await c.PostAsJsonAsync(lan.Url, new SubmitRsvp(0, "DECLINED", [])));
        await Created(await c.PostAsJsonAsync(hung.Url, new SubmitRsvp(0, "ATTENDING", [hung.Main], ["=Bạn gái Hùng"]))); // plus-one limit 0 → waits
        // Cô Hoa has not answered.

        var all = (await owner.GetFromJsonAsync<RsvpDashboardDto>($"/api/weddings/{w}/rsvps/dashboard"))!;
        Assert.Equal((4, 3, 1, 2, 1, 0, 75.0m), (all.Invitations.Invited, all.Invitations.Responded, all.Invitations.NotResponded,
            all.Invitations.Attending, all.Invitations.Declined, all.Invitations.Waiting, all.Invitations.ResponseRate));
        Assert.Equal((5, 3, 1, 1, 1, 1, 1), (all.People.InvitedPlaces, all.People.Confirmed, all.People.Companions, all.People.Waitlisted,
            all.People.Declined, all.People.UnusedPlaces, all.People.PendingPlaces));
        Assert.Equal((10, 3L, 7L), (all.Capacity.Max!.Value, all.Capacity.Occupied, all.Capacity.Remaining!.Value));
        Assert.Equal((1, 1), (all.Notes.ConfirmedWithDietaryNote, all.Notes.GuestsWithNote));
        Assert.Equal(new RsvpNoteCount("Ăn chay", 1), Assert.Single(all.Notes.TopDietaryNotes));
        Assert.Equal(new[] { new RsvpSideStats("BRIDE", 2, 1, 0), new RsvpSideStats("GROOM", 2, 2, 3) }, all.BySide.ToArray());
        var bride = (await owner.GetFromJsonAsync<RsvpDashboardDto>($"/api/weddings/{w}/rsvps/dashboard?side=BRIDE"))!;
        Assert.Equal((2, 1), (bride.Invitations.Invited, bride.Invitations.NotResponded));
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync($"/api/weddings/{w}/rsvps/dashboard?side=LEFT")).StatusCode);

        // Who still has to answer (Guest05 / committee Q6): the hosts remind them.
        var notAnswered = (await owner.GetFromJsonAsync<PagedResult<RsvpGuestRowDto>>($"/api/weddings/{w}/rsvps/dashboard/guests?status=NOT_RESPONDED"))!;
        var row = Assert.Single(notAnswered.Items);
        Assert.Equal((hoa.GuestId, "NOT_RESPONDED", "0901234567"), (row.GuestId, row.RsvpStatus, row.Phone));
        var namRow = Assert.Single((await owner.GetFromJsonAsync<PagedResult<RsvpGuestRowDto>>($"/api/weddings/{w}/rsvps/dashboard/guests?search=mai"))!.Items);
        Assert.Equal((nam.GuestId, 2, 2), (namRow.GuestId, namRow.ConfirmedPeople, namRow.InvitedPlaces));
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync($"/api/weddings/{w}/rsvps/dashboard/guests?status=MAYBE")).StatusCode);

        // A co-host with RSVP_VIEW but no GUEST_VIEW sees the numbers but not phone/email.
        using var viewer = await SignIn(factory, "dash-viewer");
        var viewerEmail = (await viewer.GetFromJsonAsync<AccountDto>("/api/auth/me"))!.Email;
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync($"/api/weddings/{w}/co-hosts", new AddCoHostInput(viewerEmail, ["RSVP_VIEW"]))).StatusCode);
        Assert.Null(Assert.Single((await viewer.GetFromJsonAsync<PagedResult<RsvpGuestRowDto>>($"/api/weddings/{w}/rsvps/dashboard/guests?status=NOT_RESPONDED"))!.Items).Phone);

        // Export for Excel: UTF-8 BOM, one line per person, formula injection neutralised, contacts only with GUEST_VIEW.
        var export = await owner.GetAsync($"/api/weddings/{w}/rsvps/dashboard/export");
        Assert.Equal("text/csv", export.Content.Headers.ContentType!.MediaType);
        var bytes = await export.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var lines = System.Text.Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(7, lines.Length); // header + Nam, Mai, Lan, Hoa, Hùng and Hùng's waiting friend
        Assert.Contains(lines, l => l.Contains(",'=Bạn gái Hùng,"));
        Assert.Contains("Số điện thoại", lines[0]);
        Assert.DoesNotContain("Số điện thoại", System.Text.Encoding.UTF8.GetString(await viewer.GetByteArrayAsync($"/api/weddings/{w}/rsvps/dashboard/export")));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task SeatedPartyThatGrowsIsFlaggedForSeating() => WithApi(async (db, factory, owner, ownerId) =>
    {
        // Main-sheet edge case: a seated guest later brings more people → the new place has no seat and the hosts are warned.
        var w = await Wedding(db, ownerId, "rsvp-grow", 10); var a = await Party(db, owner, w, "A");
        await Read<RsvpSettingsDto>(await owner.PutAsJsonAsync($"/api/weddings/{w}/rsvp-settings", new SaveRsvpSettings(0, 1)));
        using var c = factory.CreateHttpsClient();
        var first = await Created(await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", [a.Main])));
        var table = new PlanningTable { Id = Guid.NewGuid(), WeddingId = w, Name = "Bàn 1", Capacity = 10 }; db.Add(table);
        var place = await db.Set<PlanningPlace>().AsNoTracking().SingleAsync(x => x.GuestId == a.GuestId);
        db.Add(new PlanningSeatAssignment { PlaceId = place.Id, TableId = table.Id, SeatNumber = 1 }); await db.SaveChangesAsync();

        var grown = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "ATTENDING", [a.Main], ["Bạn của A"])));
        Assert.Equal((2, 2), (grown.ConfirmedPartySize, grown.Places.Count));
        var flag = await db.AuditLogs.AsNoTracking().SingleAsync(x => x.Action == "SEAT_NEEDS_ATTENTION");
        Assert.Equal(("planning_places", grown.Places[1].Id), (flag.EntityType, flag.EntityId));
        Assert.Equal("PARTY_GREW", JsonDocument.Parse(flag.Metadata!).RootElement.GetProperty("reason").GetString());
        Assert.Equal(0, await db.Set<PlanningSeatAssignment>().CountAsync(x => x.PlaceId == grown.Places[1].Id)); // RSVP never seats people
    });

    // ---------------- Security / API surface ----------------

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task HostRoutesRequireTheRightWeddingPermission() => WithApi(async (db, factory, owner, ownerId) =>
    {
        var w = await Wedding(db, ownerId, "rsvp-perm", 5); var a = await Party(db, owner, w, "A");
        var other = await Wedding(db, ownerId, "rsvp-perm-other", 5);
        using var viewer = await SignIn(factory, "viewer"); using var outsider = await SignIn(factory, "outsider");
        using var anonymous = factory.CreateHttpsClient();
        var viewerEmail = (await viewer.GetFromJsonAsync<AccountDto>("/api/auth/me"))!.Email;
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync($"/api/weddings/{w}/co-hosts", new AddCoHostInput(viewerEmail, ["RSVP_VIEW"]))).StatusCode);
        var url = $"/api/weddings/{w}/rsvps/{a.GuestId}"; var body = new SubmitRsvp(0, "ATTENDING", [a.Main]);

        foreach (var read in new[] { url, $"/api/weddings/{w}/rsvps/waitlist", $"/api/weddings/{w}/rsvps/requests", $"/api/weddings/{w}/rsvp-settings" })
            Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(read)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync(url, body)).StatusCode); // needs GUEST_EDIT
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync($"/api/weddings/{w}/rsvp-settings", new SaveRsvpSettings(0, 1))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync($"/api/weddings/{w}/rsvps/locks", new LockRsvps(true, [a.GuestId]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
        await AssertProblem(HttpStatusCode.NotFound, "GUEST_NOT_FOUND", await owner.GetAsync($"/api/weddings/{other}/rsvps/{a.GuestId}")); // other wedding
        await AssertProblem(HttpStatusCode.NotFound, "GUEST_NOT_FOUND", await owner.PutAsJsonAsync($"/api/weddings/{other}/rsvps/locks", new LockRsvps(true, [a.GuestId])));
        Assert.Equal(0, await db.Set<PlanningRsvp>().CountAsync());
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task OpenApiDocumentsTheRsvpModule() => WithApi(async (_, factory, _, _) =>
    {
        using var c = factory.CreateHttpsClient();
        var paths = (await c.GetFromJsonAsync<JsonElement>("/openapi/v1.json")).GetProperty("paths");
        var guest = paths.GetProperty("/api/rsvp/{token}");
        foreach (var verb in new[] { "get", "post", "put" })
            Assert.Equal("RSVP (Guest)", guest.GetProperty(verb).GetProperty("tags")[0].GetString());
        Assert.True(guest.GetProperty("post").GetProperty("responses").TryGetProperty("201", out _));
        Assert.True(guest.GetProperty("put").GetProperty("responses").TryGetProperty("202", out _));
        foreach (var path in new[]
                 {
                     "/api/weddings/{weddingId}/rsvps/{guestId}", "/api/weddings/{weddingId}/rsvps/waitlist", "/api/weddings/{weddingId}/rsvps/waitlist/{entryId}/promote",
                     "/api/weddings/{weddingId}/rsvps/waitlist/{entryId}/decline", "/api/weddings/{weddingId}/rsvps/requests",
                     "/api/weddings/{weddingId}/rsvps/requests/{requestId}/approve", "/api/weddings/{weddingId}/rsvps/requests/{requestId}/reject",
                     "/api/weddings/{weddingId}/rsvps/locks", "/api/weddings/{weddingId}/rsvp-settings"
                 })
            Assert.True(paths.TryGetProperty(path, out _), path);
    });

    // ---------------- seeding & helpers ----------------

    private sealed record Seeded(Guid GuestId, string Token, Guid Main)
    {
        public string Url => "/api/rsvp/" + Token;
    }

    private static async Task<Guid> Wedding(InviteMeDbContext db, Guid ownerId, string slug, int capacity)
    {
        var wedding = Domain.Weddings.Wedding.Create(ownerId, "RSVP wedding", slug, capacity);
        db.Weddings.Add(wedding); await db.SaveChangesAsync(); return wedding.Id;
    }

    // Uses the Guest Planning v2 import API, exactly like the hosts do; the token is the one behind /invite/{token}.
    private static async Task<Seeded> Party(InviteMeDbContext db, HttpClient owner, Guid weddingId, string name, int people = 1)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/weddings/{weddingId}/guest-planning/imports")
        { Content = JsonContent.Create(new PlanningImport([new PlanningRow(2, name, ExpectedPeople: people)])) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return await Seed(db, Assert.Single((await Read<PlanningImportResult>(await owner.SendAsync(request))).Items).GuestId);
    }

    private static async Task<Seeded> Seed(InviteMeDbContext db, Guid guestId)
    {
        var token = await db.Set<PlanningGuest>().AsNoTracking().Where(x => x.Id == guestId).Select(x => x.InvitationToken!).SingleAsync();
        var main = await db.Set<PlanningParticipant>().AsNoTracking().Where(x => x.GuestId == guestId).Select(x => x.Id).SingleAsync();
        return new(guestId, token, main);
    }

    private static Task<int> Confirmed(InviteMeDbContext db, Guid weddingId) =>
        (from p in db.Set<PlanningParticipant>() join g in db.Set<PlanningGuest>() on p.GuestId equals g.Id
         where g.WeddingId == weddingId && p.AttendanceStatus == "ATTENDING" select p).CountAsync();
    private static Task<List<PlanningRsvpHistory>> History(InviteMeDbContext db) =>
        db.Set<PlanningRsvpHistory>().AsNoTracking().OrderBy(x => x.ChangedAt).ThenBy(x => x.Id).ToListAsync();
    private static async Task<string> HistoryFingerprint(InviteMeDbContext db) => string.Join("\n",
        await db.Database.SqlQueryRaw<string>("SELECT row_to_json(h)::text AS \"Value\" FROM inviteme.planning_rsvp_history h ORDER BY changed_at, id").ToListAsync());

    private static Task WithApi(Func<InviteMeDbContext, DatabaseApiFactory, HttpClient, Guid, Task> test) => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        await using var factory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
        using var owner = await SignIn(factory, "owner");
        var ownerId = (await owner.GetFromJsonAsync<AccountDto>("/api/auth/me"))!.Id;
        await test(db, factory, owner, ownerId);
    });
    private static async Task<HttpClient> SignIn(DatabaseApiFactory factory, string name, bool register = true)
    {
        var c = factory.CreateHttpsClient();
        if (register) await Read<AccountDto>(await c.PostAsJsonAsync("/api/auth/register", new RegisterAccount(name + "@example.test", Password, name)));
        var token = await Read<TokenDto>(await c.PostAsJsonAsync("/api/auth/login", new LoginAccount(name + "@example.test", Password)));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken); return c;
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

    private sealed class RecordingPublisher : IPlanningChangePublisher
    {
        public ConcurrentQueue<(Guid WeddingId, PlanningChange Change)> Changes { get; } = new();
        public Task PublishAsync(Guid weddingId, PlanningChange change, CancellationToken cancellationToken)
        { Changes.Enqueue((weddingId, change)); return Task.CompletedTask; }
    }
}
