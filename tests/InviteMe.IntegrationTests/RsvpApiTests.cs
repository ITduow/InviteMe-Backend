using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Rsvps;
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
/// RSVP module (Guest03 + Guest04) through the real HTTP pipeline against real PostgreSQL, on Guest Planning v2 data.
/// Guests are created with the Guest Planning import API and answer with their planning invitation token
/// (the same token as /invite/{token}). Weddings are seeded directly because no wedding API exists yet.
/// </summary>
public sealed class RsvpApiTests
{
    private const string Password = "Test-only-Pass123!";

    // ---------------- Guest03: submit / update / history ----------------

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task FirstAnswerThenChangesAppendOneImmutableHistoryRowPerConfirmedChange() => WithApi(async (db, factory, owner, ownerId) =>
    {
        var w = await Wedding(db, ownerId, "rsvp-happy", 10); var a = await Party(db, owner, w, "Anh Nam", people: 2);
        using var c = factory.CreateHttpsClient();

        var initial = (await c.GetFromJsonAsync<RsvpDto>(a.Url))!;
        Assert.Equal(("PENDING", (Guid?)null, 0, RsvpCapacityOutcome.None, 2, 1), (initial.Status, initial.RsvpId, initial.Version, initial.CapacityOutcome, initial.InvitedPlaces, initial.OpenPlaces));
        Assert.Equal(("Anh Nam", a.Main), (initial.RecipientName, Assert.Single(initial.Participants).Id));

        var created = await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", [a.Main], ["Chị Mai"]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); Assert.Null(created.Headers.Location);
        var first = (await created.Content.ReadFromJsonAsync<RsvpDto>())!;
        Assert.Equal(("ATTENDING", 2, 1, 0, RsvpCapacityOutcome.Confirmed, (string?)null), (first.Status, first.ConfirmedPartySize, first.Version, first.OpenPlaces, first.CapacityOutcome, first.Message));
        var mai = first.Participants.Single(p => p.FullName == "Chị Mai");
        Assert.Equal(("COMPANION", "ATTENDING"), (mai.Role, mai.AttendanceStatus));
        var h1 = Assert.Single(await History(db));
        Assert.Null(h1.OldStatus); Assert.Equal(("RSVP_SUBMITTED", "WEDDING_GUEST", a.GuestId), (h1.Reason, h1.ActorType, h1.ActorGuestId!.Value));
        Assert.DoesNotContain(a.Token, h1.NewSnapshot); Assert.DoesNotContain("Mai", h1.NewSnapshot);

        // One source of truth: the Guest Planning guest detail shows the RSVP answer on every reserved place.
        var detail = await owner.GetFromJsonAsync<JsonElement>($"/api/weddings/{w}/guest-planning/guests/{a.GuestId}");
        Assert.All(detail.GetProperty("places").EnumerateArray(), p => Assert.Equal("ATTENDING", p.GetProperty("person").GetProperty("attendanceStatus").GetString()));
        var frozen = await HistoryFingerprint(db);

        await AssertProblem(HttpStatusCode.Conflict, "RSVP_ALREADY_EXISTS", await c.PostAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "DECLINED", [])));
        var reduced = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "ATTENDING", [a.Main])));
        Assert.Equal((1, "DECLINED"), (reduced.ConfirmedPartySize, reduced.Participants.Single(p => p.Id == mai.Id).AttendanceStatus));
        Assert.Equal(first.SubmittedAt, reduced.SubmittedAt);
        var same = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(reduced.Version, "ATTENDING", [a.Main])));
        Assert.Equal(reduced.Version, same.Version); // no-op
        await AssertProblem(HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT", await c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "DECLINED", [])));
        var declined = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(same.Version, "DECLINED", [])));
        Assert.Equal(("DECLINED", 0), (declined.Status, declined.ConfirmedPartySize));
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
    public Task UnnamedReservedPlacesBecomeDeclinedPlaceholdersThatCanLaterAttend() => WithApi(async (db, factory, owner, ownerId) =>
    {
        // Seating treats an empty reserved place as coming; after an answer every place carries an explicit status.
        var w = await Wedding(db, ownerId, "rsvp-places", 10); var a = await Party(db, owner, w, "Gia đình Hùng", people: 3);
        using var c = factory.CreateHttpsClient();
        var first = await Created(await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", [a.Main])));
        Assert.Equal((3, 0, 1), (first.InvitedPlaces, first.OpenPlaces, first.ConfirmedPartySize));
        Assert.Equal(new[] { "Người đi cùng 2", "Người đi cùng 3" }, first.Participants.Skip(1).Select(p => p.FullName).ToArray());
        Assert.All(first.Participants.Skip(1), p => Assert.Equal(("COMPANION", "DECLINED"), (p.Role, p.AttendanceStatus)));
        Assert.Equal(0, await db.Set<PlanningPlace>().CountAsync(x => x.GuestId == a.GuestId && x.ParticipantId == null));

        var second = first.Participants[1].Id;
        var later = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "ATTENDING", [a.Main, second])));
        Assert.Equal(2, later.ConfirmedPartySize);
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "PLUS_ONE_LIMIT", await c.PutAsJsonAsync(a.Url, new SubmitRsvp(later.Version, "ATTENDING", [a.Main], ["Extra"])));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task InvalidRequestsAndDeadlineRulesWriteNothing() => WithApi(async (db, factory, owner, ownerId) =>
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
        await AssertProblem(HttpStatusCode.NotFound, "INVITATION_NOT_FOUND", await c.GetAsync("/api/rsvp/" + new string('a', 64)));
        await AssertProblem(HttpStatusCode.NotFound, "INVITATION_NOT_FOUND", await c.GetAsync("/api/rsvp/not-a-token"));
        await AssertProblem(HttpStatusCode.NotFound, "RSVP_NOT_FOUND", await c.PutAsJsonAsync(a.Url, new SubmitRsvp(0, "DECLINED", [])));
        await AssertProblem(HttpStatusCode.NotFound, "PARTICIPANT_NOT_FOUND", await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", [other.Main])));
        await AssertProblem(HttpStatusCode.NotFound, "PARTICIPANT_NOT_FOUND", await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", [a.Main], DietaryNotes: [new(other.Main, "Chay")])));
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "PLUS_ONE_LIMIT", await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", [a.Main], ["X", "Y"])));
        await AssertProblem(HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT", await c.PostAsJsonAsync(a.Url, new SubmitRsvp(1, "ATTENDING", [a.Main])));
        Assert.Equal(0, await db.Set<PlanningRsvp>().CountAsync()); Assert.Equal(0, await db.Set<PlanningRsvpHistory>().CountAsync());
        Assert.Equal(1, await db.Set<PlanningParticipant>().CountAsync(x => x.GuestId == a.GuestId));

        // A closed wedding gives the same 404 as an unknown token (same rule as GET /api/guest-invitations/{token}).
        var closed = await Wedding(db, ownerId, "rsvp-closed", 10); var z = await Party(db, owner, closed, "Z");
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(z.Url)).StatusCode);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inviteme.weddings SET status='CANCELLED' WHERE id={closed}");
        await AssertProblem(HttpStatusCode.NotFound, "INVITATION_NOT_FOUND", await c.GetAsync(z.Url));

        // After wedding_settings.rsvp_deadline: increases rejected, notes and cancellation still accepted.
        var first = await Created(await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", [a.Main])));
        var settings = WeddingSettings.Create(w); settings.Configure("Asia/Ho_Chi_Minh", "PRIVATE", DateTimeOffset.UtcNow.AddMinutes(-1), 2);
        db.WeddingSettings.Add(settings); await db.SaveChangesAsync();
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "RSVP_DEADLINE_PASSED", await c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "ATTENDING", [a.Main, first.Participants[1].Id])));
        var noted = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "ATTENDING", [a.Main], Note: "Đến muộn 15 phút")));
        Assert.Equal("Đến muộn 15 phút", noted.Note);
        var cancelled = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(noted.Version, "DECLINED", [])));
        Assert.Equal("RSVP_LATE_CANCELLATION", (await History(db)).Last().Reason);
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "RSVP_DEADLINE_PASSED", await c.PutAsJsonAsync(a.Url, new SubmitRsvp(cancelled.Version, "ATTENDING", [a.Main])));
        Assert.Equal(2, await db.Set<PlanningRsvpHistory>().CountAsync());
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
        var w = await Wedding(db, ownerId, "rsvp-guards", 2); var a = await Party(db, owner, w, "A", people: 2);
        var c2 = await Party(db, owner, w, "C"); var d = await Party(db, owner, w, "D");
        using var c = factory.CreateHttpsClient();
        var first = await Created(await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", [a.Main])));
        await Created(await c.PostAsJsonAsync(c2.Url, new SubmitRsvp(0, "ATTENDING", [c2.Main]))); // 2/2 full
        var frozen = await HistoryFingerprint(db); var audits = await db.AuditLogs.CountAsync();
        foreach (var sql in new[] { "UPDATE inviteme.planning_rsvp_history SET reason = 'x'", "DELETE FROM inviteme.planning_rsvp_history", "TRUNCATE inviteme.planning_rsvp_history" })
            Assert.Equal(PostgresErrorCodes.RestrictViolation, (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql))).SqlState);

        // Failing history insert and failing waitlist insert: RSVP, people, places, notes, waitlist, history and audit all roll back.
        foreach (var (table, call) in new (string, Func<Task<HttpResponseMessage>>)[]
                 {
                     ("planning_rsvp_history", () => c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "DECLINED", [], Note: "x"))),
                     ("planning_rsvp_waitlist", () => c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "ATTENDING", [a.Main, first.Participants[1].Id]))),
                     ("planning_rsvp_waitlist", () => c.PostAsJsonAsync(d.Url, new SubmitRsvp(0, "ATTENDING", [d.Main])))
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
        Assert.Equal((first.Version, "ATTENDING", 1, 0, (string?)null), (state.Version, state.Status, state.ConfirmedPartySize, state.WaitlistedPartySize, state.Note));
        Assert.Equal(0, await db.Set<PlanningRsvp>().CountAsync(x => x.GuestId == d.GuestId));
        Assert.Equal("PENDING", (await db.Set<PlanningParticipant>().AsNoTracking().SingleAsync(x => x.GuestId == d.GuestId)).AttendanceStatus);
        Assert.Equal(0, await db.Set<PlanningWaitlistEntry>().CountAsync());
        Assert.Equal(frozen, await HistoryFingerprint(db)); Assert.Equal(audits, await db.AuditLogs.CountAsync());
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task SeatedGuestWhoStopsAttendingKeepsTheSeatAndIsFlaggedForTheHost() => WithApi(async (db, factory, owner, ownerId) =>
    {
        // Committee Q8 / F3-E2: RSVP never removes or moves seated guests; the host decides. Clients are told to refetch.
        var w = await Wedding(db, ownerId, "rsvp-seat-flag", 10); var a = await Party(db, owner, w, "A", people: 2);
        var events = new RecordingPublisher();
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IPlanningChangePublisher>(); s.AddSingleton<IPlanningChangePublisher>(events);
        }));
        using var c = app.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false });
        var first = await Created(await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", [a.Main], ["Mai"])));
        var published = Assert.Single(events.Changes);
        Assert.Equal((w, RsvpHandler.RsvpChanged, a.GuestId, (int?)1), (published.WeddingId, published.Change.Type, published.Change.EntityId, published.Change.Version));
        var mai = first.Participants.Single(p => p.FullName == "Mai").Id;

        var table = new PlanningTable { Id = Guid.NewGuid(), WeddingId = w, Name = "Bàn 1", Capacity = 10 }; db.Add(table);
        var places = await db.Set<PlanningPlace>().AsNoTracking().Where(x => x.GuestId == a.GuestId).OrderBy(x => x.Position).ToListAsync();
        db.AddRange(places.Select((p, i) => new PlanningSeatAssignment { PlaceId = p.Id, TableId = table.Id, SeatNumber = i + 1 }));
        await db.SaveChangesAsync();

        var reduced = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "ATTENDING", [a.Main])));
        Assert.Equal(1, reduced.ConfirmedPartySize);
        Assert.Equal(2, await db.Set<PlanningSeatAssignment>().CountAsync(x => x.TableId == table.Id)); // seat kept
        var flag = await db.AuditLogs.AsNoTracking().SingleAsync(x => x.Action == "SEAT_NEEDS_ATTENTION");
        Assert.Equal(("planning_seat_assignments", places[1].Id), (flag.EntityType, flag.EntityId));
        using (var meta = JsonDocument.Parse(flag.Metadata!))
        {
            Assert.Equal(mai, meta.RootElement.GetProperty("participantId").GetGuid());
            Assert.Equal("DECLINED", meta.RootElement.GetProperty("attendanceStatus").GetString());
        }
        Assert.Contains(events.Changes, x => x.Change.Type == RsvpHandler.SeatingNeedsAttention && x.WeddingId == w);
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

    // ---------------- Guest04: capacity / waitlist / promotion ----------------

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task CapacityGateConfirmsWhatFitsAndWaitlistsTheRest() => WithApi(async (db, factory, owner, ownerId) =>
    {
        var w = await Wedding(db, ownerId, "rsvp-gate", 3);
        var a = await Party(db, owner, w, "A", people: 2); var b = await Party(db, owner, w, "B", people: 2); var c3 = await Party(db, owner, w, "C");
        using var c = factory.CreateHttpsClient();
        var ra = await Created(await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", [a.Main], ["A2"])));
        Assert.Equal((2, RsvpCapacityOutcome.Confirmed), (ra.ConfirmedPartySize, ra.CapacityOutcome));
        var rc = await Created(await c.PostAsJsonAsync(c3.Url, new SubmitRsvp(0, "ATTENDING", [c3.Main]))); // exactly the last place
        Assert.Equal((1, RsvpCapacityOutcome.Confirmed), (rc.ConfirmedPartySize, rc.CapacityOutcome));
        var rb = await Created(await c.PostAsJsonAsync(b.Url, new SubmitRsvp(0, "ATTENDING", [b.Main], ["B2"])));
        Assert.Equal(("PENDING", 0, 2, RsvpCapacityOutcome.Waitlisted), (rb.Status, rb.ConfirmedPartySize, rb.WaitlistedPartySize, rb.CapacityOutcome));
        Assert.StartsWith("Hết chỗ", rb.Message);
        var entry = Assert.Single((await owner.GetFromJsonAsync<PagedResult<WaitlistDto>>($"/api/weddings/{w}/rsvps/waitlist"))!.Items);
        Assert.Equal((b.GuestId, 2, WaitlistKind.FullParty), (entry.GuestId, entry.RequestedSlots, entry.Kind));
        Assert.Equal(3, await Confirmed(db, w)); // waitlisted people do not consume capacity

        // Identical resubmission while still waiting keeps the same entry (queue position).
        var again = await Read<RsvpDto>(await c.PutAsJsonAsync(b.Url, new SubmitRsvp(rb.Version, "ATTENDING", rb.Participants.Select(p => p.Id).ToArray())));
        Assert.Equal(rb.Version, again.Version);
        Assert.Equal(entry.Id, Assert.Single((await owner.GetFromJsonAsync<PagedResult<WaitlistDto>>($"/api/weddings/{w}/rsvps/waitlist"))!.Items).Id);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ConfirmedPartyKeepsItsPlacesWhenOnlyTheExtraPersonWaitsThenIsPromoted() => WithApi(async (db, factory, owner, ownerId) =>
    {
        var w = await Wedding(db, ownerId, "rsvp-partial", 2);
        var a = await Party(db, owner, w, "A", people: 2); var c2 = await Party(db, owner, w, "C");
        using var c = factory.CreateHttpsClient();
        var first = await Created(await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", [a.Main])));
        var companion = first.Participants[1].Id;
        var rc = await Created(await c.PostAsJsonAsync(c2.Url, new SubmitRsvp(0, "ATTENDING", [c2.Main])));
        var historyCount = await db.Set<PlanningRsvpHistory>().CountAsync(); var frozen = await HistoryFingerprint(db);

        var partial = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "ATTENDING", [a.Main, companion])));
        Assert.Equal(("ATTENDING", 1, 1, RsvpCapacityOutcome.PartiallyWaitlisted), (partial.Status, partial.ConfirmedPartySize, partial.WaitlistedPartySize, partial.CapacityOutcome));
        Assert.Equal("ATTENDING", partial.Participants.Single(p => p.Id == a.Main).AttendanceStatus);
        Assert.Equal(historyCount, await db.Set<PlanningRsvpHistory>().CountAsync()); // waitlist-only change: audit, no history
        Assert.True(await db.AuditLogs.AnyAsync(x => x.Action == "RSVP_WAITLIST_UPDATED"));
        var entry = Assert.Single((await owner.GetFromJsonAsync<PagedResult<WaitlistDto>>($"/api/weddings/{w}/rsvps/waitlist"))!.Items);
        Assert.Equal((1, WaitlistKind.AdditionalMember), (entry.RequestedSlots, entry.Kind));
        var promote = $"/api/weddings/{w}/rsvps/waitlist/{entry.Id}/promote";
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "CAPACITY_FULL", await owner.PostAsync(promote, null));

        await Read<RsvpDto>(await c.PutAsJsonAsync(c2.Url, new SubmitRsvp(rc.Version, "DECLINED", []))); // frees a place, no auto-promotion
        Assert.True(await db.Set<PlanningParticipant>().AnyAsync(x => x.Id == companion && x.AttendanceStatus == "WAITLISTED"));
        var promoted = await Read<RsvpDto>(await owner.PostAsync(promote, null));
        Assert.Equal(("ATTENDING", 2, 0, RsvpCapacityOutcome.Confirmed), (promoted.Status, promoted.ConfirmedPartySize, promoted.WaitlistedPartySize, promoted.CapacityOutcome));
        var last = (await History(db)).Last();
        Assert.Equal(("ATTENDING", 1, 2, "WAITLIST_PROMOTED", "USER"), (last.OldStatus!, last.OldConfirmedPartySize!.Value, last.NewConfirmedPartySize, last.Reason, last.ActorType));
        var resolved = await db.Set<PlanningWaitlistEntry>().AsNoTracking().SingleAsync(x => x.Id == entry.Id);
        Assert.Equal("PROMOTED", resolved.Status); Assert.NotNull(resolved.ResolvedAt);
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "WAITLIST_NOT_WAITING", await owner.PostAsync(promote, null));
        await AssertProblem(HttpStatusCode.NotFound, "WAITLIST_NOT_FOUND", await owner.PostAsync($"/api/weddings/{w}/rsvps/waitlist/{Guid.NewGuid()}/promote", null));
        Assert.StartsWith(frozen, await HistoryFingerprint(db));
    });

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
        var states = await Task.WhenAll((await Task.WhenAll(ca.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", [a.Main], ["A2"])),
            cb.PostAsJsonAsync(b.Url, new SubmitRsvp(0, "ATTENDING", [b.Main], ["B2"])))).Select(Created));
        Assert.Single(states, x => x.CapacityOutcome == RsvpCapacityOutcome.Confirmed);
        Assert.Single(states, x => x.CapacityOutcome == RsvpCapacityOutcome.Waitlisted);
        Assert.Equal(150, await Confirmed(db, w));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ConcurrentPromotionsCannotOverfillCapacity() => WithApi(async (db, factory, owner, ownerId) =>
    {
        var w = await Wedding(db, ownerId, "rsvp-promote", 1);
        var x = await Party(db, owner, w, "X"); var a = await Party(db, owner, w, "A"); var b = await Party(db, owner, w, "B");
        using var c = factory.CreateHttpsClient();
        var rx = await Created(await c.PostAsJsonAsync(x.Url, new SubmitRsvp(0, "ATTENDING", [x.Main])));
        await Created(await c.PostAsJsonAsync(a.Url, new SubmitRsvp(0, "ATTENDING", [a.Main])));
        await Created(await c.PostAsJsonAsync(b.Url, new SubmitRsvp(0, "ATTENDING", [b.Main])));
        var entries = (await owner.GetFromJsonAsync<PagedResult<WaitlistDto>>($"/api/weddings/{w}/rsvps/waitlist"))!.Items;
        Assert.Equal(new[] { a.GuestId, b.GuestId }, entries.Select(e => e.GuestId).ToArray()); // request order
        await Read<RsvpDto>(await c.PutAsJsonAsync(x.Url, new SubmitRsvp(rx.Version, "DECLINED", [])));

        using var second = await SignIn(factory, "owner", register: false);
        var races = await Task.WhenAll(owner.PostAsync($"/api/weddings/{w}/rsvps/waitlist/{entries[0].Id}/promote", null),
            second.PostAsync($"/api/weddings/{w}/rsvps/waitlist/{entries[1].Id}/promote", null));
        Assert.Single(races, r => r.StatusCode == HttpStatusCode.OK);
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "CAPACITY_FULL", Assert.Single(races, r => r.StatusCode != HttpStatusCode.OK));
        Assert.Equal(1, await Confirmed(db, w));

        var winner = (await db.Set<PlanningWaitlistEntry>().AsNoTracking().SingleAsync(e => e.Status == "PROMOTED")).GuestId == a.GuestId ? a : b;
        var ws = (await c.GetFromJsonAsync<RsvpDto>(winner.Url))!;
        await Read<RsvpDto>(await c.PutAsJsonAsync(winner.Url, new SubmitRsvp(ws.Version, "DECLINED", [])));
        var waitingId = (await db.Set<PlanningWaitlistEntry>().AsNoTracking().SingleAsync(e => e.Status == "WAITING")).Id;
        var twice = await Task.WhenAll(owner.PostAsync($"/api/weddings/{w}/rsvps/waitlist/{waitingId}/promote", null),
            second.PostAsync($"/api/weddings/{w}/rsvps/waitlist/{waitingId}/promote", null));
        Assert.Single(twice, r => r.StatusCode == HttpStatusCode.OK);
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "WAITLIST_NOT_WAITING", Assert.Single(twice, r => r.StatusCode != HttpStatusCode.OK));
        Assert.Equal(1, await Confirmed(db, w));
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

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/weddings/{w}/rsvps/waitlist")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync(url, body)).StatusCode); // needs GUEST_EDIT
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
        await AssertProblem(HttpStatusCode.NotFound, "GUEST_NOT_FOUND", await owner.GetAsync($"/api/weddings/{other}/rsvps/{a.GuestId}")); // other wedding
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
        foreach (var path in new[] { "/api/weddings/{weddingId}/rsvps/{guestId}", "/api/weddings/{weddingId}/rsvps/waitlist", "/api/weddings/{weddingId}/rsvps/waitlist/{entryId}/promote" })
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
        var guestId = Assert.Single((await Read<PlanningImportResult>(await owner.SendAsync(request))).Items).GuestId;
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
