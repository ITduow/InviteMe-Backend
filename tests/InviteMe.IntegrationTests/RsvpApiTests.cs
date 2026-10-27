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
/// RSVP module (Guest03 + Couple06 rules) through the real HTTP pipeline against real PostgreSQL, on Guest
/// Planning v2 data. Guests are created with the Guest Planning import API and answer with their planning invitation token
/// (the same token as /invite/{token}). Weddings are seeded directly to keep each test focused.
/// </summary>
public sealed class RsvpApiTests
{
    private const string Password = "Test-only-Pass123!";

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task WeddingWithThirtyImportedInvitationsFlowsThroughRsvpAndSeating() => WithApi(async (db, factory, owner, _) =>
    {
        var eventAt = DateTimeOffset.UtcNow.AddMonths(6);
        var wedding = await Read<WeddingSetupDto>(await owner.PostAsJsonAsync("/api/weddings",
            new CreateWeddingInput("Thirty guest RSVP flow", "rsvp-flow-thirty", eventAt,
                "Test hall", "Test address", 80, eventAt.AddDays(-10))));
        var w = wedding.Id;
        await PlanApiTests.UseBasicPlan(db, w); // 70 expected people: more than the free plan allows
        var emptyDraft = await owner.GetAsync($"/api/weddings/{w}/invitation-messages/draft");
        Assert.Equal(HttpStatusCode.OK, emptyDraft.StatusCode);
        Assert.Equal("null", await emptyDraft.Content.ReadAsStringAsync());
        var rows = Enumerable.Range(0, 30).Select(i => new PlanningRow(i + 2, $"QA guest {i + 1:00}",
            Honorific: i % 3 == 0 ? "Bác" : i % 3 == 1 ? "Anh" : "Bạn",
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
        var undecided = await Seed(db, imported.Items[4].GuestId);
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
        Assert.Equal((4, "PENDING", 4), (initial.Places.Count, initial.Status, initial.MaxPeople));
        var answered = await Created(await guest.PostAsJsonAsync(family.Url, new SubmitRsvp(0, "ATTENDING", 3, Note: "Gần lối đi")));
        Assert.Equal((3, "UNUSED", false), (answered.PartySize, answered.Places[3].Status, answered.OverLimit));
        Assert.Equal(["QA guest 01", "Khách đi kèm 1 của QA guest 01", "Khách đi kèm 2 của QA guest 01"], answered.Participants.Select(p => p.FullName));
        Assert.Contains(events.Changes, e => e.WeddingId == w && e.Change.Type == RsvpHandler.RsvpChanged);
        Assert.Contains(events.Changes, e => e.WeddingId == w && e.Change.Type == RsvpHandler.SeatingNeedsAttention);
        plan = (await owner.GetFromJsonAsync<PlanningSeatingDto>(seatingUrl))!;
        Assert.Equal(4, plan.Assignments.Count);
        Assert.Null(plan.Places.Single(p => p.Id == familyPlaces[3]).Person);

        await Created(await guest.PostAsJsonAsync(individual.Url, new SubmitRsvp(0, "DECLINED")));
        await Created(await guest.PostAsJsonAsync(undecided.Url, new SubmitRsvp(0, "MAYBE", 2)));
        // 2 people invited, plus-one limit 0: an answer of 3 is saved and counted, the couple is warned.
        var over = await Created(await guest.PostAsJsonAsync(group.Url, new SubmitRsvp(0, "ATTENDING", 3)));
        Assert.Equal((3, 2, true), (over.PartySize, over.MaxPeople, over.OverLimit));
        Assert.Contains(events.Changes, e => e.Change.Type == RsvpHandler.RsvpOverLimit && e.Change.EntityId == group.GuestId);
        var row = Assert.Single((await owner.GetFromJsonAsync<PagedResult<RsvpGuestRowDto>>($"/api/weddings/{w}/rsvps/dashboard/guests?status=NEEDS_ACTION"))!.Items);
        Assert.Equal((group.GuestId, "QA guest 02", 3, true), (row.GuestId, row.RecipientName, row.People, row.OverLimit));
        var seen = await Read<RsvpDto>(await owner.PostAsync($"/api/weddings/{w}/rsvps/{group.GuestId}/acknowledge", null));
        Assert.Equal((3, false), (seen.PartySize, seen.OverLimit));
        Assert.Empty((await owner.GetFromJsonAsync<PagedResult<RsvpGuestRowDto>>($"/api/weddings/{w}/rsvps/dashboard/guests?status=NEEDS_ACTION"))!.Items);

        await Read<LockRsvpsResult>(await owner.PutAsJsonAsync($"/api/weddings/{w}/rsvps/locks", new LockRsvps(true, [lockedGuest.GuestId])));
        var queued = await guest.PostAsJsonAsync(lockedGuest.Url, new SubmitRsvp(0, "ATTENDING", 1));
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        Assert.Equal(1, (await owner.GetFromJsonAsync<RsvpDashboardDto>($"/api/weddings/{w}/rsvps/dashboard"))!.NeedsAction.ChangeRequests);
        var request = Assert.Single((await owner.GetFromJsonAsync<PagedResult<RsvpRequestDto>>($"/api/weddings/{w}/rsvps/requests"))!.Items);
        await Read<RsvpDto>(await owner.PostAsync($"/api/weddings/{w}/rsvps/requests/{request.Id}/approve", null));

        var dashboard = (await owner.GetFromJsonAsync<RsvpDashboardDto>($"/api/weddings/{w}/rsvps/dashboard"))!;
        Assert.Equal((30, 3, 1, 1, 25), (dashboard.Invitations.Invited, dashboard.Invitations.Attending, dashboard.Invitations.Maybe,
            dashboard.Invitations.Declined, dashboard.Invitations.NotResponded));
        Assert.Equal((7, 2, 73L), (dashboard.People.Expected, dashboard.People.Maybe, dashboard.Capacity.Remaining!.Value));
        Assert.Equal(0, dashboard.NeedsAction.Invitations);
        Assert.Equal(15, dashboard.BySide.Single(x => x.Side == "BRIDE").Invited);
        Assert.Equal(15, dashboard.BySide.Single(x => x.Side == "GROOM").Invited);
        Assert.Equal(25, (await owner.GetFromJsonAsync<PagedResult<RsvpGuestRowDto>>(
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
        Assert.Equal(("PENDING", (Guid?)null, 0, 0, 2, false, (string?)null),
            (initial.Status, initial.RsvpId, initial.Version, initial.PartySize, initial.MaxPeople, initial.Locked, initial.ThankYouMessage));
        Assert.Equal(new[] { RsvpPlaceStatus.Pending, RsvpPlaceStatus.Pending }, initial.Places.Select(p => p.Status).ToArray());
        Assert.Equal(("Anh Nam", a.Main), (initial.RecipientName, Assert.Single(initial.Participants).Id));

        var created = await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", 2));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); Assert.Null(created.Headers.Location);
        var first = (await created.Content.ReadFromJsonAsync<RsvpDto>())!;
        Assert.Equal(("ATTENDING", 2, 1, false, (string?)null),
            (first.Status, first.PartySize, first.Version, first.OverLimit, first.Message));
        Assert.StartsWith("Cảm ơn bạn đã xác nhận", first.ThankYouMessage);
        Assert.All(first.Places, p => Assert.Equal(RsvpPlaceStatus.Used, p.Status));
        var companion = first.Participants.Single(p => p.Role == "COMPANION");
        Assert.Equal(("Khách đi kèm 1 của Anh Nam", "ATTENDING"), (companion.FullName, companion.AttendanceStatus));
        var h1 = Assert.Single(await History(db));
        Assert.Null(h1.OldStatus); Assert.Equal(("RSVP_SUBMITTED", "WEDDING_GUEST", a.GuestId), (h1.Reason, h1.ActorType, h1.ActorGuestId!.Value));
        Assert.DoesNotContain(a.Token, h1.NewSnapshot); Assert.DoesNotContain("Khách đi kèm", h1.NewSnapshot);

        // One source of truth: the Guest Planning guest detail shows the RSVP answer of each person.
        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/weddings/{w}/guest-planning/guests/{a.GuestId}");
        Assert.All(detail.GetProperty("places").EnumerateArray(), p =>
            Assert.Equal("ATTENDING", p.GetProperty("person").GetProperty("attendanceStatus").GetString()));
        var frozen = await HistoryFingerprint(db);

        await AssertProblem(HttpStatusCode.Conflict, "RSVP_ALREADY_EXISTS", await c.PostAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "DECLINED")));
        var reduced = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "ATTENDING", 1)));
        Assert.Equal((1, "DECLINED"), (reduced.PartySize, reduced.Participants.Single(p => p.Id == companion.Id).AttendanceStatus));
        Assert.Equal(new[] { RsvpPlaceStatus.Used, RsvpPlaceStatus.Unused }, reduced.Places.Select(p => p.Status).ToArray());
        Assert.Equal(first.SubmittedAt, reduced.SubmittedAt);
        var same = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(reduced.Version, "ATTENDING", 1)));
        Assert.Equal(reduced.Version, same.Version); // no-op
        await AssertProblem(HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT", await c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "DECLINED")));
        var declined = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(same.Version, "DECLINED")));
        Assert.Equal(("DECLINED", 0), (declined.Status, declined.PartySize));
        Assert.Contains("Rất tiếc", declined.ThankYouMessage);
        Assert.All(declined.Places, p => Assert.Equal(RsvpPlaceStatus.Unused, p.Status));
        var all = await History(db);
        Assert.Equal(3, all.Count); Assert.Equal(("ATTENDING", 1, "DECLINED", 0), (all[2].OldStatus, all[2].OldConfirmedPartySize!.Value, all[2].NewStatus, all[2].NewConfirmedPartySize));
        Assert.StartsWith(frozen, await HistoryFingerprint(db)); // earlier rows unchanged

        // Couple route: answer on the guest's behalf by planning guest id, recorded with a USER actor. The companion is reused.
        await Read<RsvpDto>(await owner.PutAsJsonAsync($"/api/weddings/{w}/rsvps/{a.GuestId}", new SubmitRsvp(declined.Version, "ATTENDING", 2)));
        var host = (await History(db)).Last();
        Assert.Equal(("USER", ownerId), (host.ActorType, host.ChangedBy!.Value));
        var byHost = (await owner.GetFromJsonAsync<RsvpDto>($"/api/weddings/{w}/rsvps/{a.GuestId}"))!;
        Assert.Equal((2, 2), (byHost.PartySize, byHost.Participants.Count));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task PlacesLeftEmptyBecomeUnusedWithoutFakePeople() => WithApi(async (db, factory, owner, ownerId) =>
    {
        // RSVP DOC: family of 4, three come, the fourth place stays empty → 3 people, 4 places, place 4 UNUSED.
        var w = await Wedding(db, ownerId, "rsvp-places", 10); var an = await Party(db, owner, w, "Nguyễn Văn An", people: 4);
        using var c = factory.CreateHttpsClient();
        var first = await Created(await c.PostAsJsonAsync(an.Url, new SubmitRsvp(0, "ATTENDING", 3)));
        Assert.Equal((3, 3, 4), (first.PartySize, first.Participants.Count, first.Places.Count));
        Assert.Equal(new[] { "USED", "USED", "USED", "UNUSED" }, first.Places.Select(p => p.Status).ToArray());
        Assert.Null(first.Places[3].ParticipantId);
        Assert.Equal(3, await db.Set<PlanningParticipant>().CountAsync(x => x.GuestId == an.GuestId));
        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/weddings/{w}/guest-planning/guests/{an.GuestId}");
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("places")[3].GetProperty("person").ValueKind); // nothing written on Guest Planning data

        // Later the fourth person comes after all: the empty place is filled, no new place is created.
        var later = await Read<RsvpDto>(await c.PutAsJsonAsync(an.Url, new SubmitRsvp(first.Version, "ATTENDING", 4)));
        Assert.Equal((4, 4), (later.PartySize, later.Places.Count));
        Assert.All(later.Places, p => Assert.Equal(RsvpPlaceStatus.Used, p.Status));
        Assert.Equal("Khách đi kèm 3 của Nguyễn Văn An", later.Participants[3].FullName);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task MaybeTakesNoPlaceIsCountedApartAndALaterYesIsCountedEvenOverCapacity() => WithApi(async (db, factory, owner, ownerId) =>
    {
        // Meeting 26/10: "Phân vân" is not counted in the capacity, only shown as "Phân vân: n người".
        var w = await Wedding(db, ownerId, "rsvp-maybe", 3);
        var a = await Party(db, owner, w, "Mai", people: 2); var b = await Party(db, owner, w, "Bảo", people: 2);
        using var c = factory.CreateHttpsClient();
        var maybe = await Created(await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "MAYBE", 2)));
        Assert.Equal(("MAYBE", 2, false), (maybe.Status, maybe.PartySize, maybe.OverLimit));
        Assert.Contains("đổi câu trả lời", maybe.ThankYouMessage);
        Assert.All(maybe.Places, p => Assert.Equal(RsvpPlaceStatus.Pending, p.Status));
        Assert.Equal("PENDING", Assert.Single(maybe.Participants).AttendanceStatus); // no companion is created for a MAYBE
        Assert.Equal(0, await Confirmed(db, w));

        await Created(await c.PostAsJsonAsync(b.Url, new SubmitRsvp(0, "ATTENDING", 2)));
        var dashboard = (await owner.GetFromJsonAsync<RsvpDashboardDto>($"/api/weddings/{w}/rsvps/dashboard"))!;
        Assert.Equal((1, 1, 2, 2), (dashboard.Invitations.Maybe, dashboard.Invitations.Attending, dashboard.People.Maybe, dashboard.People.Expected));
        Assert.True(dashboard.Capacity.MaybeMayExceed); // 2 coming + 2 maybe > 3

        // Mai now says yes for 2 although only 1 place is left: both are counted, the couple only sees a capacity warning.
        var yes = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(maybe.Version, "ATTENDING", 2)));
        Assert.Equal(("ATTENDING", 2, false, (string?)null), (yes.Status, yes.PartySize, yes.OverLimit, yes.Message));
        Assert.Equal(4, await Confirmed(db, w));
        var capacity = (await owner.GetFromJsonAsync<RsvpDashboardDto>($"/api/weddings/{w}/rsvps/dashboard"))!.Capacity;
        Assert.Equal((3, 4L, 0L, true, 1L), (capacity.Max, capacity.Expected, capacity.Remaining, capacity.OverCapacity, capacity.OverBy));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task AnswersOverTheThresholdAreSavedAndFlaggedUntilTheCoupleHandlesThem() => WithApi(async (db, factory, owner, ownerId) =>
    {
        // 27/10: "đăng ký bao nhiêu cho đi bấy nhiêu". Threshold = people invited on the invitation + plus-one limit (wedding setup).
        // Going over it is never refused: the couple is warned and either enters the number again or marks it as seen.
        var w = await Wedding(db, ownerId, "rsvp-plus-one", 100);
        var settings = await Read<RsvpSettingsDto>(await owner.PutAsJsonAsync($"/api/weddings/{w}/rsvp-settings", new SaveRsvpSettings(0, 1)));
        Assert.Equal((1, 1), (settings.PlusOneLimit, settings.Version));
        await AssertProblem(HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT", await owner.PutAsJsonAsync($"/api/weddings/{w}/rsvp-settings", new SaveRsvpSettings(0, 2)));
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/weddings/{w}/rsvp-settings", new SaveRsvpSettings(1, 21))).StatusCode);
        var single = await Party(db, owner, w, "Lan"); var other = await Party(db, owner, w, "Hùng"); var family = await Party(db, owner, w, "Bác An", people: 4);
        var events = new RecordingPublisher();
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        { s.RemoveAll<IPlanningChangePublisher>(); s.AddSingleton<IPlanningChangePublisher>(events); }));
        using var c = app.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
        using var host = app.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
        host.DefaultRequestHeaders.Authorization = owner.DefaultRequestHeaders.Authorization;

        Assert.Equal(2, (await c.GetFromJsonAsync<RsvpDto>(single.Url))!.MaxPeople); // 1 invited + 1
        Assert.Equal(5, (await c.GetFromJsonAsync<RsvpDto>(family.Url))!.MaxPeople); // 4 invited + 1
        var lan = await Created(await c.PostAsJsonAsync(single.Url, new SubmitRsvp(0, "ATTENDING", 2)));
        Assert.Equal((2, 2, false), (lan.PartySize, lan.Places.Count, lan.OverLimit)); // a new place for the companion

        var hung = await Created(await c.PostAsJsonAsync(other.Url, new SubmitRsvp(0, "ATTENDING", 3)));
        Assert.Equal(("ATTENDING", 3, 3, true), (hung.Status, hung.PartySize, hung.Places.Count, hung.OverLimit));
        Assert.All(hung.Participants, p => Assert.Equal("ATTENDING", p.AttendanceStatus));
        Assert.Contains("tối đa 2 người", hung.Message);
        Assert.Single(events.Changes, e => e.Change.Type == RsvpHandler.RsvpOverLimit && e.Change.EntityId == other.GuestId);
        Assert.False((await Created(await c.PostAsJsonAsync(family.Url, new SubmitRsvp(0, "ATTENDING", 5)))).OverLimit); // exactly the threshold
        var familyOver = (await c.GetFromJsonAsync<RsvpDto>(family.Url))!;
        familyOver = await Read<RsvpDto>(await c.PutAsJsonAsync(family.Url, new SubmitRsvp(familyOver.Version, "ATTENDING", 6)));
        Assert.True(familyOver.OverLimit);

        var dashboard = (await owner.GetFromJsonAsync<RsvpDashboardDto>($"/api/weddings/{w}/rsvps/dashboard"))!;
        Assert.Equal((2, 2, 0), (dashboard.NeedsAction.Invitations, dashboard.NeedsAction.OverLimit, dashboard.NeedsAction.ChangeRequests));
        Assert.Equal(11, dashboard.People.Expected); // 2 + 3 + 6: everyone counts

        // "Đã xem": the warning goes off, the number stays, and the guest's version is unchanged (no conflict on the form).
        var seen = await Read<RsvpDto>(await host.PostAsync($"/api/weddings/{w}/rsvps/{other.GuestId}/acknowledge", null));
        Assert.Equal((3, false, hung.Version, (string?)null), (seen.PartySize, seen.OverLimit, seen.Version, seen.Message));
        await Read<RsvpDto>(await host.PostAsync($"/api/weddings/{w}/rsvps/{other.GuestId}/acknowledge", null)); // twice = no-op
        Assert.Equal(1, await db.AuditLogs.CountAsync(x => x.Action == "RSVP_OVER_LIMIT_ACCEPTED"));
        Assert.False((await Read<RsvpDto>(await c.PutAsJsonAsync(other.Url, new SubmitRsvp(seen.Version, "ATTENDING", 2)))).OverLimit);
        var again = (await c.GetFromJsonAsync<RsvpDto>(other.Url))!;
        var grew = await Read<RsvpDto>(await c.PutAsJsonAsync(other.Url, new SubmitRsvp(again.Version, "ATTENDING", 4)));
        Assert.True(grew.OverLimit); // more than the number the couple has seen → warned again
        Assert.Equal(2, events.Changes.Count(e => e.Change.Type == RsvpHandler.RsvpOverLimit && e.Change.EntityId == other.GuestId));

        // Or the couple calls the guest and enters the number on his behalf: that number counts as seen.
        var byHost = await Read<RsvpDto>(await host.PutAsJsonAsync($"/api/weddings/{w}/rsvps/{family.GuestId}",
            new SubmitRsvp(familyOver.Version, "ATTENDING", 6, Source: RsvpSource.Phone)));
        Assert.Equal((6, false), (byHost.PartySize, byHost.OverLimit));
        Assert.Equal(1, (await owner.GetFromJsonAsync<RsvpDashboardDto>($"/api/weddings/{w}/rsvps/dashboard"))!.NeedsAction.OverLimit);

        // Only an ATTENDING answer can be marked as seen.
        var nobody = await Party(db, owner, w, "Chưa trả lời");
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "RSVP_NOT_ATTENDING", await host.PostAsync($"/api/weddings/{w}/rsvps/{nobody.GuestId}/acknowledge", null));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task WalkInsAddedAtReceptionAreCountedInTheExpectedPeople() => WithApi(async (db, factory, owner, ownerId) =>
    {
        // Cohost04 walk-ins are planning people with ATTENDING, so RSVP counts them (the old walk_ins table is not used).
        var w = await Wedding(db, ownerId, "rsvp-walkin-capacity", 3);
        var a = await Party(db, owner, w, "A", people: 2);
        using var walkIn = new HttpRequestMessage(HttpMethod.Post, $"/api/weddings/{w}/guest-planning/walk-ins")
        { Content = JsonContent.Create(new InviteMe.Application.Features.Guests.Reception.AddWalkIn(null, "Khách vãng lai", 2)) };
        walkIn.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.True((await owner.SendAsync(walkIn)).IsSuccessStatusCode);
        using var c = factory.CreateHttpsClient();
        var answer = await Created(await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", 2)));
        Assert.Equal(2, answer.PartySize); // only one place was left: still saved
        var dashboard = (await owner.GetFromJsonAsync<RsvpDashboardDto>($"/api/weddings/{w}/rsvps/dashboard"))!;
        Assert.Equal((3, 4L, 0L, true, 1L), (dashboard.Capacity.Max, dashboard.Capacity.Expected, dashboard.Capacity.Remaining,
            dashboard.Capacity.OverCapacity, dashboard.Capacity.OverBy));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task AFullWeddingNeverRefusesAnAnswerItOnlyWarnsTheCouple() => WithApi(async (db, factory, owner, ownerId) =>
    {
        var w = await Wedding(db, ownerId, "rsvp-slots", 3);
        var a = await Party(db, owner, w, "A", people: 2); var b = await Party(db, owner, w, "B", people: 2); var z = await Party(db, owner, w, "Z");
        using var c = factory.CreateHttpsClient();
        await Created(await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", 2)));
        Assert.Equal(2, (await Created(await c.PostAsJsonAsync(b.Url, new SubmitRsvp(0, "ATTENDING", 2)))).PartySize);
        var rz = await Created(await c.PostAsJsonAsync(z.Url, new SubmitRsvp(0, "ATTENDING", 1)));
        Assert.Equal(("ATTENDING", 1, (string?)null), (rz.Status, rz.PartySize, rz.Message));
        // The couple entering an answer is not refused either.
        Assert.Equal(2, (await Read<RsvpDto>(await owner.PutAsJsonAsync($"/api/weddings/{w}/rsvps/{z.GuestId}",
            new SubmitRsvp(rz.Version, "ATTENDING", 2, Source: RsvpSource.Phone)))).PartySize);
        Assert.Equal(6, await Confirmed(db, w));
        var capacity = (await owner.GetFromJsonAsync<RsvpDashboardDto>($"/api/weddings/{w}/rsvps/dashboard"))!.Capacity;
        Assert.Equal((3, 6L, 0L, true, 3L), (capacity.Max, capacity.Expected, capacity.Remaining, capacity.OverCapacity, capacity.OverBy));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task AfterTheDeadlineOrALockGuestChangesWaitForTheHosts() => WithApi(async (db, factory, owner, ownerId) =>
    {
        // Couple06 + committee Q4: after the deadline (or when the couple locks a guest / a group) the guest can only send a
        // change request; numbers change only when the couple approves it.
        var w = await Wedding(db, ownerId, "rsvp-lock", 10);
        var a = await Party(db, owner, w, "A", people: 2);
        using var c = factory.CreateHttpsClient();
        var first = await Created(await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", 2)));
        var settings = WeddingSettings.Create(w); settings.Configure("Asia/Ho_Chi_Minh", "PRIVATE", DateTimeOffset.UtcNow.AddMinutes(-1), 2);
        db.WeddingSettings.Add(settings); await db.SaveChangesAsync();

        var late = await c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "ATTENDING", 1));
        Assert.Equal(HttpStatusCode.Accepted, late.StatusCode);
        var queued = (await late.Content.ReadFromJsonAsync<RsvpDto>())!;
        Assert.Equal((first.Version, 2, true, RsvpMessages.Locked), (queued.Version, queued.PartySize, queued.Locked, queued.Message));
        Assert.Equal(("DEADLINE_PASSED", "PENDING"), (queued.PendingRequest!.Reason, queued.PendingRequest.Status));
        var newer = (await (await c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "DECLINED"))).Content.ReadFromJsonAsync<RsvpDto>())!;
        Assert.Equal("DECLINED", newer.PendingRequest!.Change.Decision); // the latest change replaces the older request
        Assert.Equal(1, await db.Set<PlanningRsvpHistory>().CountAsync());

        var requests = (await owner.GetFromJsonAsync<PagedResult<RsvpRequestDto>>($"/api/weddings/{w}/rsvps/requests"))!;
        var request = Assert.Single(requests.Items);
        Assert.Equal(("A", "DECLINED"), (request.RecipientName, request.Change.Decision));
        Assert.Single((await owner.GetFromJsonAsync<PagedResult<RsvpRequestDto>>($"/api/weddings/{w}/rsvps/requests?status=SUPERSEDED"))!.Items);
        var applied = await Read<RsvpDto>(await owner.PostAsync($"/api/weddings/{w}/rsvps/requests/{request.Id}/approve", null));
        Assert.Equal(("DECLINED", 0, (RsvpRequestDto?)null), (applied.Status, applied.PartySize, applied.PendingRequest));
        Assert.Equal(("RSVP_CHANGE_APPROVED", "USER"), ((await History(db)).Last().Reason, (await History(db)).Last().ActorType));
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "REQUEST_NOT_PENDING", await owner.PostAsync($"/api/weddings/{w}/rsvps/requests/{request.Id}/approve", null));

        // Rejecting keeps the RSVP as it is.
        var again = (await (await c.PutAsJsonAsync(a.Url, new SubmitRsvp(applied.Version, "ATTENDING", 1))).Content.ReadFromJsonAsync<RsvpDto>())!;
        var rejected = await Read<RsvpDto>(await owner.PostAsync($"/api/weddings/{w}/rsvps/requests/{again.PendingRequest!.Id}/reject", null));
        Assert.Equal(("DECLINED", (RsvpRequestDto?)null), (rejected.Status, rejected.PendingRequest));

        // The couple locks a whole group before the deadline: the group's guests can only send requests; the couple still edits directly.
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
        Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsJsonAsync(g1.Url, new SubmitRsvp(0, "ATTENDING", 1))).StatusCode);
        Assert.True((await c.GetFromJsonAsync<RsvpDto>(g1.Url))!.Locked);
        // The couple entering the answer directly closes the guest's waiting request: it no longer counts as pending.
        var direct = await Read<RsvpDto>(await owner.PutAsJsonAsync($"/api/weddings/{open}/rsvps/{g1.GuestId}", new SubmitRsvp(0, "ATTENDING", 1, Source: RsvpSource.Phone)));
        Assert.Null(direct.PendingRequest);
        Assert.Empty((await owner.GetFromJsonAsync<PagedResult<RsvpRequestDto>>($"/api/weddings/{open}/rsvps/requests"))!.Items);
        var closed = Assert.Single((await owner.GetFromJsonAsync<PagedResult<RsvpRequestDto>>($"/api/weddings/{open}/rsvps/requests?status=SUPERSEDED"))!.Items);
        Assert.Equal(g1.GuestId, closed.GuestId);
        Assert.Equal(2, (await Read<LockRsvpsResult>(await owner.PutAsJsonAsync($"/api/weddings/{open}/rsvps/locks", new LockRsvps(false, GroupId: groupId)))).Affected);
        var g2 = await Seed(db, imported.Items[1].GuestId);
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync(g2.Url, new SubmitRsvp(0, "ATTENDING", 1))).StatusCode);

        // A request saved before the switch to "number of people" (participantIds / plusOnes) is still listed and applied.
        var legacy = await Party(db, owner, open, "Khách cũ");
        db.Add(PlanningRsvpRequest.Create(open, legacy.GuestId,
            $$"""{"expectedVersion":0,"decision":"ATTENDING","participantIds":["{{legacy.Main}}"],"plusOnes":["Bạn"]}""", "LOCKED", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
        var old = Assert.Single((await owner.GetFromJsonAsync<PagedResult<RsvpRequestDto>>($"/api/weddings/{open}/rsvps/requests"))!.Items);
        Assert.Equal(("ATTENDING", (int?)2), (old.Change.Decision, old.Change.PartySize));
        Assert.Equal(2, (await Read<RsvpDto>(await owner.PostAsync($"/api/weddings/{open}/rsvps/requests/{old.Id}/approve", null))).PartySize);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task InvalidRequestsWriteNothing() => WithApi(async (db, factory, owner, ownerId) =>
    {
        var w = await Wedding(db, ownerId, "rsvp-invalid", 10); var a = await Party(db, owner, w, "A", people: 2);
        using var c = factory.CreateHttpsClient();
        foreach (var body in new object[]
                 {
                     new SubmitRsvp(0, "DECLINED", 2), new SubmitRsvp(0, "ATTENDING"), new SubmitRsvp(0, "ATTENDING", 0), new SubmitRsvp(0, "MAYBE"),
                     new SubmitRsvp(0, "PENDING", 1), new SubmitRsvp(-1, "ATTENDING", 1), new SubmitRsvp(0, "ATTENDING", 51),
                     new SubmitRsvp(0, "ATTENDING", 1, Note: new string('x', 501)), new SubmitRsvp(0, "ATTENDING", 1, DietaryNote: new string('x', 201)),
                     new { expectedVersion = 0, decision = "ATTENDING", participantIds = new[] { a.Main } } // the old form fields are refused
                 })
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync(a.Url, body)).StatusCode);
        await AssertProblem(HttpStatusCode.NotFound, "INVITATION_UNAVAILABLE", await c.GetAsync("/api/rsvp/" + new string('a', 64)));
        await AssertProblem(HttpStatusCode.NotFound, "INVITATION_UNAVAILABLE", await c.GetAsync("/api/rsvp/not-a-token"));
        await AssertProblem(HttpStatusCode.NotFound, "RSVP_NOT_FOUND", await c.PutAsJsonAsync(a.Url, new SubmitRsvp(0, "DECLINED")));
        await AssertProblem(HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT", await c.PostAsJsonAsync(a.Url, new SubmitRsvp(1, "ATTENDING", 1)));
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
        var first = await Created(await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", 2, Note: "  Xin bàn gần sân khấu  ", DietaryNote: "Ăn chay")));
        Assert.Equal(("Xin bàn gần sân khấu", "Ăn chay"), (first.Note, first.DietaryNote));
        Assert.Equal(1, await db.Set<PlanningRsvpHistory>().CountAsync());
        Assert.DoesNotContain("chay", (await History(db)).Single().NewSnapshot, StringComparison.OrdinalIgnoreCase);

        // Only notes change → saved, version advances, no RSVP history row.
        var changed = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "ATTENDING", 2, Note: "Đi xe riêng")));
        Assert.Equal(("Đi xe riêng", first.Version + 1, "Ăn chay"), (changed.Note, changed.Version, changed.DietaryNote)); // omitted = unchanged
        Assert.Equal(1, await db.Set<PlanningRsvpHistory>().CountAsync());
        var audit = await db.AuditLogs.AsNoTracking().SingleAsync(x => x.Action == "RSVP_NOTE_UPDATED");
        Assert.Equal(a.GuestId, JsonDocument.Parse(audit.Metadata!).RootElement.GetProperty("guestId").GetGuid());

        // Empty note / blank dietary note clear them.
        var cleared = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(changed.Version, "ATTENDING", 2, Note: "", DietaryNote: " ")));
        Assert.Equal(((string?)null, (string?)null), (cleared.Note, cleared.DietaryNote));
        Assert.Equal(0, await db.Set<PlanningDietaryNote>().CountAsync());
        var again = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(cleared.Version, "ATTENDING", 2, Note: "")));
        Assert.Equal(cleared.Version, again.Version); // nothing to change → no-op
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task HistoryIsAppendOnlyAndFailedWritesRollBack() => WithApi(async (db, factory, owner, ownerId) =>
    {
        var w = await Wedding(db, ownerId, "rsvp-guards", 10); var a = await Party(db, owner, w, "A"); var d = await Party(db, owner, w, "D");
        using var c = factory.CreateHttpsClient();
        var first = await Created(await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", 1)));
        var frozen = await HistoryFingerprint(db); var audits = await db.AuditLogs.CountAsync();
        foreach (var sql in new[] { "UPDATE inviteme.planning_rsvp_history SET reason = 'x'", "DELETE FROM inviteme.planning_rsvp_history", "TRUNCATE inviteme.planning_rsvp_history" })
            Assert.Equal(PostgresErrorCodes.RestrictViolation, (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql))).SqlState);

        // Failing history / place / person inserts: RSVP, people, places, notes, history and audit all roll back.
        foreach (var (table, call) in new (string, Func<Task<HttpResponseMessage>>)[]
                 {
                     ("planning_rsvp_history", () => c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "DECLINED", Note: "x"))),
                     ("planning_places", () => c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "ATTENDING", 2))),
                     ("planning_participants", () => c.PostAsJsonAsync(d.Url, new SubmitRsvp(0, "ATTENDING", 2)))
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
        Assert.Equal((first.Version, "ATTENDING", 1, (string?)null, 1, 1), (state.Version, state.Status, state.PartySize, state.Note, state.Participants.Count, state.Places.Count));
        Assert.Equal(0, await db.Set<PlanningRsvp>().CountAsync(x => x.GuestId == d.GuestId));
        Assert.Equal(("PENDING", 1), ((await db.Set<PlanningParticipant>().AsNoTracking().SingleAsync(x => x.GuestId == d.GuestId)).AttendanceStatus,
            await db.Set<PlanningPlace>().CountAsync(x => x.GuestId == d.GuestId)));
        Assert.Equal(frozen, await HistoryFingerprint(db)); Assert.Equal(audits, await db.AuditLogs.CountAsync());
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task SeatedPlacesThatStopBeingUsedKeepTheSeatAndAreFlaggedForTheHost() => WithApi(async (db, factory, owner, ownerId) =>
    {
        // Couple06 / committee Q8: family of 4 seated at table T1, only 3 come → seat 4 stays (red for the couple), never removed.
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

        var first = await Created(await c.PostAsJsonAsync(an.Url, new SubmitRsvp(0, "ATTENDING", 3)));
        Assert.Contains(events.Changes, x => (x.WeddingId, x.Change.Type, x.Change.EntityId, x.Change.Version) == (w, RsvpHandler.RsvpChanged, an.GuestId, (int?)1));
        Assert.Contains(events.Changes, x => x.Change.Type == RsvpHandler.SeatingNeedsAttention && x.WeddingId == w);
        Assert.Equal(4, await db.Set<PlanningSeatAssignment>().CountAsync(x => x.TableId == table.Id)); // seat 4 kept
        var flag = await db.AuditLogs.AsNoTracking().SingleAsync(x => x.Action == "SEAT_NEEDS_ATTENTION");
        Assert.Equal(("planning_seat_assignments", places[3].Id), (flag.EntityType, flag.EntityId));

        // One fewer later: the companion with the highest number stops attending, his seat is flagged too, still not removed.
        var second = first.Participants.Single(p => p.FullName == "Khách đi kèm 2 của Bác An").Id;
        var two = await Read<RsvpDto>(await c.PutAsJsonAsync(an.Url, new SubmitRsvp(first.Version, "ATTENDING", 2)));
        Assert.Equal("DECLINED", two.Participants.Single(p => p.Id == second).AttendanceStatus);
        var flags = await db.AuditLogs.AsNoTracking().Where(x => x.Action == "SEAT_NEEDS_ATTENTION").OrderBy(x => x.CreatedAt).ToListAsync();
        Assert.Equal(2, flags.Count);
        using (var meta = JsonDocument.Parse(flags[1].Metadata!))
        {
            Assert.Equal(second, meta.RootElement.GetProperty("participantId").GetGuid());
            Assert.Equal(RsvpPlaceStatus.Unused, meta.RootElement.GetProperty("placeStatus").GetString());
        }
        // Switching to MAYBE: the two seats in use are flagged (now PENDING); seats already unused are not flagged again.
        await Read<RsvpDto>(await c.PutAsJsonAsync(an.Url, new SubmitRsvp(two.Version, "MAYBE", 2)));
        Assert.Equal(4, await db.AuditLogs.CountAsync(x => x.Action == "SEAT_NEEDS_ATTENTION"));
        Assert.Equal(4, await db.Set<PlanningSeatAssignment>().CountAsync(x => x.TableId == table.Id));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task AnswerSourceIsRecordedForHostEntriesAndRejectedOnTheGuestLink() => WithApi(async (db, factory, owner, ownerId) =>
    {
        // Committee Q6: the couple enters answers received by phone/chat/in person; the source is kept in the audit log.
        var w = await Wedding(db, ownerId, "rsvp-source", 10); var a = await Party(db, owner, w, "A"); var b = await Party(db, owner, w, "B");
        using var c = factory.CreateHttpsClient();
        await AssertProblem(HttpStatusCode.BadRequest, "VALIDATION_FAILED", await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", 1, Source: RsvpSource.Phone)));
        await Created(await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", 1)));
        var host = $"/api/weddings/{w}/rsvps/{b.GuestId}";
        await AssertProblem(HttpStatusCode.BadRequest, "VALIDATION_FAILED", await owner.PutAsJsonAsync(host, new SubmitRsvp(0, "ATTENDING", 1, Source: "FAX")));
        await Read<RsvpDto>(await owner.PutAsJsonAsync(host, new SubmitRsvp(0, "ATTENDING", 1, Source: RsvpSource.Phone)));
        var sources = await db.AuditLogs.AsNoTracking().Where(x => x.EntityType == "planning_rsvps").OrderBy(x => x.CreatedAt)
            .Select(x => new { x.ActorType, x.Metadata }).ToListAsync();
        Assert.Equal(2, sources.Count);
        var guestLink = JsonDocument.Parse(sources[0].Metadata!).RootElement;
        Assert.Equal(("SYSTEM", RsvpSource.GuestLink, a.GuestId), (sources[0].ActorType, guestLink.GetProperty("source").GetString(), guestLink.GetProperty("guestId").GetGuid()));
        Assert.Equal(("USER", RsvpSource.Phone), (sources[1].ActorType, JsonDocument.Parse(sources[1].Metadata!).RootElement.GetProperty("source").GetString()));
    });

    // ---------------- concurrency ----------------

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ConcurrentAnswersAreAllCountedAndTheSameInvitationIsWrittenOnce() => WithApi(async (db, factory, owner, ownerId) =>
    {
        // Capacity 150 with 148 coming, two +2 answers at once: both are saved (152), the dashboard warns the couple.
        var w = await Wedding(db, ownerId, "rsvp-race", 150);
        var filler = await Party(db, owner, w, "FILL");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inviteme.planning_participants SET \"AttendanceStatus\"='ATTENDING' WHERE \"GuestId\"={filler.GuestId}");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inviteme.planning_participants(\"Id\", \"GuestId\", \"FullName\", \"Role\", \"AttendanceStatus\") SELECT gen_random_uuid(), {filler.GuestId}, 'Filler ' || i, 'COMPANION', 'ATTENDING' FROM generate_series(1, 147) AS i");
        var a = await Party(db, owner, w, "A", people: 2); var b = await Party(db, owner, w, "B", people: 2);
        using var ca = factory.CreateHttpsClient(); using var cb = factory.CreateHttpsClient();
        var races = await Task.WhenAll(ca.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", 2)), cb.PostAsJsonAsync(b.Url, new SubmitRsvp(0, "ATTENDING", 2)));
        foreach (var r in races) Assert.Equal(2, (await Created(r)).PartySize);
        Assert.Equal(152, await Confirmed(db, w));
        Assert.Equal(2L, (await owner.GetFromJsonAsync<RsvpDashboardDto>($"/api/weddings/{w}/rsvps/dashboard"))!.Capacity.OverBy);

        // Two first answers for the same invitation at once (double click): one is saved, the other is told to refresh.
        var c2 = await Party(db, owner, w, "C", people: 2);
        var twice = await Task.WhenAll(ca.PostAsJsonAsync(c2.Url, new SubmitRsvp(0, "ATTENDING", 2)), cb.PostAsJsonAsync(c2.Url, new SubmitRsvp(0, "ATTENDING", 1)));
        Assert.Single(twice, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Single(twice, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, await db.Set<PlanningRsvpHistory>().CountAsync(x => db.Set<PlanningRsvp>().Any(r => r.Id == x.RsvpId && r.GuestId == c2.GuestId)));
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
                new PlanningRow(5, "Bạn Hùng", Side: "GROOM"),
                new PlanningRow(6, "Cô Mai", Side: "BRIDE")]))
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var items = (await Read<PlanningImportResult>(await owner.SendAsync(request))).Items;
        var (nam, lan, hoa, hung, mai) = (await Seed(db, items[0].GuestId), await Seed(db, items[1].GuestId), await Seed(db, items[2].GuestId),
            await Seed(db, items[3].GuestId), await Seed(db, items[4].GuestId));
        using var c = factory.CreateHttpsClient();
        await Created(await c.PostAsJsonAsync(nam.Url, new SubmitRsvp(0, "ATTENDING", 2, Note: "Gần sân khấu", DietaryNote: "Ăn chay")));
        await Created(await c.PostAsJsonAsync(lan.Url, new SubmitRsvp(0, "DECLINED")));
        await Created(await c.PostAsJsonAsync(hung.Url, new SubmitRsvp(0, "ATTENDING", 2, Note: "=1+1"))); // plus-one limit 0 → over the threshold
        await Created(await c.PostAsJsonAsync(mai.Url, new SubmitRsvp(0, "MAYBE", 3)));
        // Cô Hoa has not answered.

        var all = (await owner.GetFromJsonAsync<RsvpDashboardDto>($"/api/weddings/{w}/rsvps/dashboard"))!;
        Assert.Equal((5, 2, 1, 1, 1, 60.0m), (all.Invitations.Invited, all.Invitations.Attending, all.Invitations.Maybe,
            all.Invitations.Declined, all.Invitations.NotResponded, all.Invitations.ResponseRate));
        Assert.Equal((1, 1, 0), (all.NeedsAction.Invitations, all.NeedsAction.OverLimit, all.NeedsAction.ChangeRequests));
        Assert.Equal((4, 2, 3), (all.People.Expected, all.People.Companions, all.People.Maybe));
        Assert.Equal((10, 4L, 6L, false, 0L, false), (all.Capacity.Max!.Value, all.Capacity.Expected, all.Capacity.Remaining!.Value,
            all.Capacity.OverCapacity, all.Capacity.OverBy, all.Capacity.MaybeMayExceed));
        Assert.Equal((1, 2), (all.Notes.InvitationsWithDietaryNote, all.Notes.InvitationsWithNote));
        Assert.Equal(new RsvpNoteCount("Ăn chay", 1), Assert.Single(all.Notes.TopDietaryNotes));
        Assert.Equal(new[] { new RsvpSideStats("BRIDE", 3, 2, 0), new RsvpSideStats("GROOM", 2, 2, 4) }, all.BySide.ToArray());
        var bride = (await owner.GetFromJsonAsync<RsvpDashboardDto>($"/api/weddings/{w}/rsvps/dashboard?side=BRIDE"))!;
        Assert.Equal((3, 1), (bride.Invitations.Invited, bride.Invitations.NotResponded));
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync($"/api/weddings/{w}/rsvps/dashboard?side=LEFT")).StatusCode);

        // Who still has to answer (Guest05 / committee Q6): the couple reminds them.
        var notAnswered = (await owner.GetFromJsonAsync<PagedResult<RsvpGuestRowDto>>($"/api/weddings/{w}/rsvps/dashboard/guests?status=NOT_RESPONDED"))!;
        var row = Assert.Single(notAnswered.Items);
        Assert.Equal((hoa.GuestId, "NOT_RESPONDED", "0901234567"), (row.GuestId, row.RsvpStatus, row.Phone));
        var namRow = Assert.Single((await owner.GetFromJsonAsync<PagedResult<RsvpGuestRowDto>>($"/api/weddings/{w}/rsvps/dashboard/guests?search=nam"))!.Items);
        Assert.Equal((nam.GuestId, 2, 2, "Ăn chay"), (namRow.GuestId, namRow.People, namRow.MaxPeople, namRow.DietaryNote));
        var maiRow = Assert.Single((await owner.GetFromJsonAsync<PagedResult<RsvpGuestRowDto>>($"/api/weddings/{w}/rsvps/dashboard/guests?status=MAYBE"))!.Items);
        Assert.Equal((mai.GuestId, 3), (maiRow.GuestId, maiRow.MaybePeople));
        var hungRow = Assert.Single((await owner.GetFromJsonAsync<PagedResult<RsvpGuestRowDto>>($"/api/weddings/{w}/rsvps/dashboard/guests?status=NEEDS_ACTION"))!.Items);
        Assert.Equal((hung.GuestId, 2, 1, true, true), (hungRow.GuestId, hungRow.People, hungRow.MaxPeople, hungRow.OverLimit, hungRow.NeedsAction));
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync($"/api/weddings/{w}/rsvps/dashboard/guests?status=WAITING")).StatusCode);

        // A co-host with RSVP_VIEW but no GUEST_VIEW sees the numbers but not phone/email.
        using var viewer = await SignIn(factory, "dash-viewer");
        await CoHostPermissionTests.AddCoHostAsync(owner, viewer, w, "RSVP_VIEW");
        Assert.Null(Assert.Single((await viewer.GetFromJsonAsync<PagedResult<RsvpGuestRowDto>>($"/api/weddings/{w}/rsvps/dashboard/guests?status=NOT_RESPONDED"))!.Items).Phone);

        // Export for Excel: UTF-8 BOM, one line per invitation, formula injection neutralised, contacts only with GUEST_VIEW.
        var export = await owner.GetAsync($"/api/weddings/{w}/rsvps/dashboard/export");
        Assert.Equal("application/octet-stream", export.Content.Headers.ContentType!.MediaType); // not text/*: Swagger UI keeps the BOM
        var bytes = await export.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var lines = System.Text.Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(6, lines.Length); // header + 5 invitations
        Assert.Contains(lines, l => l.Contains(",'=1+1,"));
        Assert.Contains("Số điện thoại", lines[0]);
        Assert.DoesNotContain("Số điện thoại", System.Text.Encoding.UTF8.GetString(await viewer.GetByteArrayAsync($"/api/weddings/{w}/rsvps/dashboard/export")));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task SeatedPartyThatGrowsIsFlaggedForSeating() => WithApi(async (db, factory, owner, ownerId) =>
    {
        // Main-sheet edge case: a seated guest later brings more people → the new place has no seat and the couple is warned.
        var w = await Wedding(db, ownerId, "rsvp-grow", 10); var a = await Party(db, owner, w, "A");
        await Read<RsvpSettingsDto>(await owner.PutAsJsonAsync($"/api/weddings/{w}/rsvp-settings", new SaveRsvpSettings(0, 1)));
        using var c = factory.CreateHttpsClient();
        var first = await Created(await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", 1)));
        var table = new PlanningTable { Id = Guid.NewGuid(), WeddingId = w, Name = "Bàn 1", Capacity = 10 }; db.Add(table);
        var place = await db.Set<PlanningPlace>().AsNoTracking().SingleAsync(x => x.GuestId == a.GuestId);
        db.Add(new PlanningSeatAssignment { PlaceId = place.Id, TableId = table.Id, SeatNumber = 1 }); await db.SaveChangesAsync();

        var grown = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "ATTENDING", 2)));
        Assert.Equal((2, 2), (grown.PartySize, grown.Places.Count));
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
        await CoHostPermissionTests.AddCoHostAsync(owner, viewer, w, "RSVP_VIEW");
        var url = $"/api/weddings/{w}/rsvps/{a.GuestId}"; var body = new SubmitRsvp(0, "ATTENDING", 1);

        foreach (var read in new[] { url, $"/api/weddings/{w}/rsvps/requests", $"/api/weddings/{w}/rsvp-settings" })
            Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(read)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync(url, body)).StatusCode); // needs GUEST_EDIT
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync(url + "/acknowledge", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync($"/api/weddings/{w}/rsvp-settings", new SaveRsvpSettings(0, 1))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync($"/api/weddings/{w}/rsvps/locks", new LockRsvps(true, [a.GuestId]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
        await AssertProblem(HttpStatusCode.NotFound, "GUEST_NOT_FOUND", await owner.GetAsync($"/api/weddings/{other}/rsvps/{a.GuestId}")); // other wedding
        await AssertProblem(HttpStatusCode.NotFound, "GUEST_NOT_FOUND", await owner.PostAsync($"/api/weddings/{other}/rsvps/{a.GuestId}/acknowledge", null));
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
                     "/api/weddings/{weddingId}/rsvps/{guestId}", "/api/weddings/{weddingId}/rsvps/{guestId}/acknowledge",
                     "/api/weddings/{weddingId}/rsvps/requests", "/api/weddings/{weddingId}/rsvps/requests/{requestId}/approve",
                     "/api/weddings/{weddingId}/rsvps/requests/{requestId}/reject", "/api/weddings/{weddingId}/rsvps/locks", "/api/weddings/{weddingId}/rsvp-settings"
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
