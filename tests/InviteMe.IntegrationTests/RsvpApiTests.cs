using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Rsvps;
using InviteMe.Application.Features.Weddings.CoHosts;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Invitations;
using InviteMe.Domain.Rsvps;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Authentication;
using InviteMe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace InviteMe.IntegrationTests;

/// <summary>
/// RSVP module (Guest03 + Guest04) through the real HTTP pipeline against real PostgreSQL.
/// Weddings, guests and published invitations are seeded directly in the database because their APIs are owned by other modules.
/// </summary>
public sealed class RsvpApiTests
{
    private const string Password = "Test-only-Pass123!";

    // ---------------- Guest03: submit / update / history ----------------

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task FirstAnswerThenChangesAppendOneImmutableHistoryRowPerConfirmedChange() => WithApi(async (db, factory, owner, ownerId) =>
    {
        var w = await Wedding(db, ownerId, "rsvp-happy", 10); var a = await Party(db, w, ownerId, "A", companions: 1);
        using var c = factory.CreateHttpsClient();

        var initial = (await c.GetFromJsonAsync<RsvpDto>(a.Url))!;
        Assert.Equal(("PENDING", (Guid?)null, RsvpCapacityOutcome.None), (initial.Status, initial.RsvpId, initial.CapacityOutcome));

        var created = await c.PostAsJsonAsync(a.Url, new SubmitRsvp(initial.Version, "ATTENDING", [a.Main]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); Assert.Null(created.Headers.Location);
        var first = (await created.Content.ReadFromJsonAsync<RsvpDto>())!;
        Assert.Equal(("ATTENDING", 1, RsvpCapacityOutcome.Confirmed, (string?)null), (first.Status, first.ConfirmedPartySize, first.CapacityOutcome, first.Message));
        var h1 = Assert.Single(await History(db));
        Assert.Null(h1.OldStatus); Assert.Equal(("RSVP_SUBMITTED", "WEDDING_GUEST", a.GuestId), (h1.Reason, h1.ActorType, h1.ActorGuestId!.Value));
        Assert.DoesNotContain(a.Token, h1.NewSnapshot);
        var frozen = await HistoryFingerprint(db);

        await AssertProblem(HttpStatusCode.Conflict, "RSVP_ALREADY_EXISTS", await c.PostAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "DECLINED", [])));
        var grown = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "ATTENDING", [a.Main, a.Others[0]])));
        Assert.Equal(2, grown.ConfirmedPartySize); Assert.Equal(first.SubmittedAt, grown.SubmittedAt);
        var same = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(grown.Version, "ATTENDING", [a.Others[0], a.Main])));
        Assert.Equal(grown.Version, same.Version); // no-op
        await AssertProblem(HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT", await c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "DECLINED", [])));
        var declined = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(same.Version, "DECLINED", [])));
        Assert.Equal(("DECLINED", 0), (declined.Status, declined.ConfirmedPartySize));
        var all = await History(db);
        Assert.Equal(3, all.Count); Assert.Equal(("ATTENDING", 2, "DECLINED", 0), (all[2].OldStatus, all[2].OldConfirmedPartySize!.Value, all[2].NewStatus, all[2].NewConfirmedPartySize));
        Assert.StartsWith(frozen, await HistoryFingerprint(db)); // earlier rows unchanged

        // Host route: create/replace on the guest's behalf, recorded with a USER actor.
        await Read<RsvpDto>(await owner.PutAsJsonAsync($"/api/weddings/{w}/rsvps/{a.InvitationId}", new SubmitRsvp(declined.Version, "ATTENDING", [a.Main])));
        var host = (await History(db)).Last();
        Assert.Equal(("USER", ownerId), (host.ActorType, host.ChangedBy!.Value));
        Assert.Equal(1, (await owner.GetFromJsonAsync<RsvpDto>($"/api/weddings/{w}/rsvps/{a.InvitationId}"))!.ConfirmedPartySize);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task InvalidRequestsAndDeadlineRulesWriteNothing() => WithApi(async (db, factory, _, ownerId) =>
    {
        var w = await Wedding(db, ownerId, "rsvp-invalid", 10); var a = await Party(db, w, ownerId, "A", companions: 1);
        var other = await Party(db, w, ownerId, "B");
        using var c = factory.CreateHttpsClient(); var v = a.Version;
        foreach (var body in new object[]
                 {
                     new SubmitRsvp(v, "DECLINED", [a.Main]), new SubmitRsvp(v, "ATTENDING", []), new SubmitRsvp(v, "MAYBE", [a.Main]),
                     new SubmitRsvp(0, "ATTENDING", [a.Main]), new SubmitRsvp(v, "ATTENDING", [a.Main], Note: new string('x', 501)),
                     new SubmitRsvp(v, "ATTENDING", [a.Main], DietaryNotes: [new(a.Main, new string('x', 201))]),
                     new { expectedVersion = v, decision = "ATTENDING", participantIds = new[] { a.Main }, unknown = 1 }
                 })
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync(a.Url, body)).StatusCode);
        await AssertProblem(HttpStatusCode.NotFound, "INVITATION_NOT_FOUND", await c.GetAsync("/api/rsvp/" + new string('a', 64)));
        await AssertProblem(HttpStatusCode.NotFound, "INVITATION_NOT_FOUND", await c.GetAsync("/api/rsvp/not-a-token"));
        await AssertProblem(HttpStatusCode.NotFound, "RSVP_NOT_FOUND", await c.PutAsJsonAsync(a.Url, new SubmitRsvp(v, "DECLINED", [])));
        await AssertProblem(HttpStatusCode.NotFound, "PARTICIPANT_NOT_FOUND", await c.PostAsJsonAsync(a.Url, new SubmitRsvp(v, "ATTENDING", [other.Main])));
        await AssertProblem(HttpStatusCode.NotFound, "PARTICIPANT_NOT_FOUND", await c.PostAsJsonAsync(a.Url, new SubmitRsvp(v, "ATTENDING", [a.Main], DietaryNotes: [new(other.Main, "Chay")])));
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "PLUS_ONE_LIMIT", await c.PostAsJsonAsync(a.Url, new SubmitRsvp(v, "ATTENDING", [a.Main], ["Extra"])));
        Assert.Equal(0, await db.Rsvps.CountAsync()); Assert.Equal(0, await db.RsvpHistory.CountAsync());

        // Revoked invitation token → same 404.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inviteme.invitations SET status='REVOKED' WHERE id={other.InvitationId}");
        await AssertProblem(HttpStatusCode.NotFound, "INVITATION_NOT_FOUND", await c.GetAsync(other.Url));

        // After wedding_settings.rsvp_deadline: increases rejected, notes and cancellation still accepted.
        var first = await Created(await c.PostAsJsonAsync(a.Url, new SubmitRsvp(v, "ATTENDING", [a.Main])));
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inviteme.wedding_settings SET rsvp_deadline={DateTimeOffset.UtcNow.AddMinutes(-1)} WHERE wedding_id={w}");
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "RSVP_DEADLINE_PASSED", await c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "ATTENDING", [a.Main, a.Others[0]])));
        var noted = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "ATTENDING", [a.Main], Note: "Đến muộn 15 phút")));
        Assert.Equal("Đến muộn 15 phút", noted.Note);
        var cancelled = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(noted.Version, "DECLINED", [])));
        Assert.Equal("RSVP_LATE_CANCELLATION", (await History(db)).Last().Reason);
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "RSVP_DEADLINE_PASSED", await c.PutAsJsonAsync(a.Url, new SubmitRsvp(cancelled.Version, "ATTENDING", [a.Main])));
        Assert.Equal(2, await db.RsvpHistory.CountAsync());
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task NotesAndDietaryNotesAreSavedWithoutRsvpHistory() => WithApi(async (db, factory, _, ownerId) =>
    {
        var w = await Wedding(db, ownerId, "rsvp-notes", 10); var a = await Party(db, w, ownerId, "A", companions: 1);
        using var c = factory.CreateHttpsClient();
        var first = await Created(await c.PostAsJsonAsync(a.Url, new SubmitRsvp(a.Version, "ATTENDING", [a.Main, a.Others[0]],
            Note: "  Xin bàn gần sân khấu  ", DietaryNotes: [new(a.Others[0], "Ăn chay")])));
        Assert.Equal("Xin bàn gần sân khấu", first.Note);
        Assert.Equal("Ăn chay", first.Participants.Single(p => p.Id == a.Others[0]).DietaryNote);
        Assert.Null(first.Participants.Single(p => p.Id == a.Main).DietaryNote);
        Assert.Equal(1, await db.RsvpHistory.CountAsync());
        Assert.DoesNotContain("chay", (await History(db)).Single().NewSnapshot, StringComparison.OrdinalIgnoreCase);

        // Only the note changes → saved, version advances, no RSVP history row.
        var changed = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "ATTENDING", [a.Main, a.Others[0]], Note: "Đi xe riêng")));
        Assert.Equal(("Đi xe riêng", first.Version + 1), (changed.Note, changed.Version));
        Assert.Equal("Ăn chay", changed.Participants.Single(p => p.Id == a.Others[0]).DietaryNote); // omitted = unchanged
        Assert.Equal(1, await db.RsvpHistory.CountAsync());
        Assert.True(await db.AuditLogs.AnyAsync(x => x.Action == "RSVP_NOTE_UPDATED" && x.ActorType == "WEDDING_GUEST"));

        // Empty note / null dietary note clear them; one RSVP note row per guest.
        var cleared = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(changed.Version, "ATTENDING", [a.Main, a.Others[0]], Note: "", DietaryNotes: [new(a.Others[0], null)])));
        Assert.Null(cleared.Note); Assert.All(cleared.Participants, p => Assert.Null(p.DietaryNote));
        Assert.Equal(0, await db.GuestNotes.CountAsync());
        var again = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(cleared.Version, "ATTENDING", [a.Main, a.Others[0]], Note: "")));
        Assert.Equal(cleared.Version, again.Version); // nothing to change → no-op
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inviteme.guest_notes(guest_id, note_type, content) VALUES ({a.GuestId}, 'RSVP', 'one')");
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inviteme.guest_notes(guest_id, note_type, content) VALUES ({a.GuestId}, 'RSVP', 'two')"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task HistoryIsAppendOnlyAndFailedWritesRollBack() => WithApi(async (db, factory, _, ownerId) =>
    {
        var w = await Wedding(db, ownerId, "rsvp-guards", 2); var a = await Party(db, w, ownerId, "A", companions: 1);
        var c2 = await Party(db, w, ownerId, "C"); var d = await Party(db, w, ownerId, "D");
        using var c = factory.CreateHttpsClient();
        var first = await Created(await c.PostAsJsonAsync(a.Url, new SubmitRsvp(a.Version, "ATTENDING", [a.Main])));
        await Created(await c.PostAsJsonAsync(c2.Url, new SubmitRsvp(c2.Version, "ATTENDING", [c2.Main]))); // 2/2 full
        var frozen = await HistoryFingerprint(db); var audits = await db.AuditLogs.CountAsync();
        foreach (var sql in new[] { "UPDATE inviteme.rsvp_history SET reason = 'x'", "DELETE FROM inviteme.rsvp_history", "TRUNCATE inviteme.rsvp_history" })
            Assert.Equal(PostgresErrorCodes.RestrictViolation, (await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql))).SqlState);

        // Failing history insert and failing waitlist insert: RSVP, participants, notes, waitlist, history and audit all roll back.
        foreach (var (table, call) in new (string, Func<Task<HttpResponseMessage>>)[]
                 {
                     ("rsvp_history", () => c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "DECLINED", [], Note: "x"))),
                     ("waitlist_entries", () => c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "ATTENDING", [a.Main, a.Others[0]]))),
                     ("waitlist_entries", () => c.PostAsJsonAsync(d.Url, new SubmitRsvp(d.Version, "ATTENDING", [d.Main])))
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
        Assert.Equal(0, await db.Rsvps.CountAsync(x => x.InvitationId == d.InvitationId));
        Assert.Equal(0, await db.WaitlistEntries.CountAsync()); Assert.Equal(0, await db.GuestNotes.CountAsync());
        Assert.Equal(frozen, await HistoryFingerprint(db)); Assert.Equal(audits, await db.AuditLogs.CountAsync());
    });

    // ---------------- Guest04: capacity / waitlist / promotion ----------------

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task CapacityGateConfirmsWhatFitsAndWaitlistsTheRest() => WithApi(async (db, factory, owner, ownerId) =>
    {
        var w = await Wedding(db, ownerId, "rsvp-gate", 3);
        var a = await Party(db, w, ownerId, "A", companions: 1); var b = await Party(db, w, ownerId, "B", companions: 1); var c3 = await Party(db, w, ownerId, "C");
        using var c = factory.CreateHttpsClient();
        var ra = await Created(await c.PostAsJsonAsync(a.Url, new SubmitRsvp(a.Version, "ATTENDING", a.All)));
        Assert.Equal((2, RsvpCapacityOutcome.Confirmed), (ra.ConfirmedPartySize, ra.CapacityOutcome));
        var rc = await Created(await c.PostAsJsonAsync(c3.Url, new SubmitRsvp(c3.Version, "ATTENDING", c3.All))); // exactly the last place
        Assert.Equal((1, RsvpCapacityOutcome.Confirmed), (rc.ConfirmedPartySize, rc.CapacityOutcome));
        var rb = await Created(await c.PostAsJsonAsync(b.Url, new SubmitRsvp(b.Version, "ATTENDING", b.All)));
        Assert.Equal(("PENDING", 0, 2, RsvpCapacityOutcome.Waitlisted), (rb.Status, rb.ConfirmedPartySize, rb.WaitlistedPartySize, rb.CapacityOutcome));
        Assert.StartsWith("Hết chỗ", rb.Message);
        var entry = Assert.Single((await owner.GetFromJsonAsync<PagedResult<WaitlistDto>>($"/api/weddings/{w}/rsvps/waitlist"))!.Items);
        Assert.Equal((b.GuestId, 2, WaitlistKind.FullParty), (entry.GuestId, entry.RequestedSlots, entry.Kind));
        Assert.Equal(3, await Confirmed(db, w)); // waitlisted people do not consume capacity

        // Identical resubmission while still waiting keeps the same entry (queue position).
        var again = await Read<RsvpDto>(await c.PutAsJsonAsync(b.Url, new SubmitRsvp(rb.Version, "ATTENDING", b.All)));
        Assert.Equal(rb.Version, again.Version);
        Assert.Equal(entry.Id, Assert.Single((await owner.GetFromJsonAsync<PagedResult<WaitlistDto>>($"/api/weddings/{w}/rsvps/waitlist"))!.Items).Id);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ConfirmedPartyKeepsItsPlacesWhenOnlyTheExtraPersonWaitsThenIsPromoted() => WithApi(async (db, factory, owner, ownerId) =>
    {
        var w = await Wedding(db, ownerId, "rsvp-partial", 2);
        var a = await Party(db, w, ownerId, "A", companions: 1); var c2 = await Party(db, w, ownerId, "C");
        using var c = factory.CreateHttpsClient();
        var first = await Created(await c.PostAsJsonAsync(a.Url, new SubmitRsvp(a.Version, "ATTENDING", [a.Main])));
        var rc = await Created(await c.PostAsJsonAsync(c2.Url, new SubmitRsvp(c2.Version, "ATTENDING", c2.All)));
        var historyCount = await db.RsvpHistory.CountAsync(); var frozen = await HistoryFingerprint(db);

        var partial = await Read<RsvpDto>(await c.PutAsJsonAsync(a.Url, new SubmitRsvp(first.Version, "ATTENDING", a.All)));
        Assert.Equal(("ATTENDING", 1, 1, RsvpCapacityOutcome.PartiallyWaitlisted), (partial.Status, partial.ConfirmedPartySize, partial.WaitlistedPartySize, partial.CapacityOutcome));
        Assert.Equal("ATTENDING", partial.Participants.Single(p => p.Id == a.Main).AttendanceStatus);
        Assert.Equal(historyCount, await db.RsvpHistory.CountAsync()); // waitlist-only change: audit, no history
        Assert.True(await db.AuditLogs.AnyAsync(x => x.Action == "RSVP_WAITLIST_UPDATED"));
        var entry = Assert.Single((await owner.GetFromJsonAsync<PagedResult<WaitlistDto>>($"/api/weddings/{w}/rsvps/waitlist"))!.Items);
        Assert.Equal((1, WaitlistKind.AdditionalMember), (entry.RequestedSlots, entry.Kind));
        var promote = $"/api/weddings/{w}/rsvps/waitlist/{entry.Id}/promote";
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "CAPACITY_FULL", await owner.PostAsync(promote, null));

        await Read<RsvpDto>(await c.PutAsJsonAsync(c2.Url, new SubmitRsvp(rc.Version, "DECLINED", []))); // frees a place, no auto-promotion
        Assert.True(await db.GuestParticipants.AnyAsync(x => x.Id == a.Others[0] && x.AttendanceStatus == "WAITLISTED"));
        var promoted = await Read<RsvpDto>(await owner.PostAsync(promote, null));
        Assert.Equal(("ATTENDING", 2, 0, RsvpCapacityOutcome.Confirmed), (promoted.Status, promoted.ConfirmedPartySize, promoted.WaitlistedPartySize, promoted.CapacityOutcome));
        var last = (await History(db)).Last();
        Assert.Equal(("ATTENDING", 1, 2, "WAITLIST_PROMOTED", "USER"), (last.OldStatus!, last.OldConfirmedPartySize!.Value, last.NewConfirmedPartySize, last.Reason!, last.ActorType));
        Assert.NotNull((await db.WaitlistEntries.AsNoTracking().SingleAsync(x => x.Id == entry.Id)).PromotedAt);
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "WAITLIST_NOT_WAITING", await owner.PostAsync(promote, null));
        await AssertProblem(HttpStatusCode.NotFound, "WAITLIST_NOT_FOUND", await owner.PostAsync($"/api/weddings/{w}/rsvps/waitlist/{Guid.NewGuid()}/promote", null));
        Assert.StartsWith(frozen, await HistoryFingerprint(db));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ConcurrentRequestsForTheLastPlacesNeverOverbook() => WithApi(async (db, factory, _, ownerId) =>
    {
        // architecture.md acceptance: capacity 150 with 148 attending, two independent +2 requests at once.
        var w = await Wedding(db, ownerId, "rsvp-race", 150);
        var filler = await Party(db, w, ownerId, "FILL");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inviteme.guest_participants SET attendance_status='ATTENDING' WHERE guest_id={filler.GuestId}");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO inviteme.guest_participants(guest_id, full_name, participant_type, attendance_status) SELECT {filler.GuestId}, 'Filler ' || i, 'CHILD', 'ATTENDING' FROM generate_series(1, 147) AS i");
        var a = await Party(db, w, ownerId, "A", companions: 1); var b = await Party(db, w, ownerId, "B", companions: 1);
        using var ca = factory.CreateHttpsClient(); using var cb = factory.CreateHttpsClient();
        var states = await Task.WhenAll((await Task.WhenAll(ca.PostAsJsonAsync(a.Url, new SubmitRsvp(a.Version, "ATTENDING", a.All)),
            cb.PostAsJsonAsync(b.Url, new SubmitRsvp(b.Version, "ATTENDING", b.All)))).Select(Created));
        Assert.Single(states, x => x.CapacityOutcome == RsvpCapacityOutcome.Confirmed);
        Assert.Single(states, x => x.CapacityOutcome == RsvpCapacityOutcome.Waitlisted);
        Assert.Equal(150, await Confirmed(db, w));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ConcurrentPromotionsCannotOverfillCapacity() => WithApi(async (db, factory, owner, ownerId) =>
    {
        var w = await Wedding(db, ownerId, "rsvp-promote", 1);
        var x = await Party(db, w, ownerId, "X"); var a = await Party(db, w, ownerId, "A"); var b = await Party(db, w, ownerId, "B");
        using var c = factory.CreateHttpsClient();
        var rx = await Created(await c.PostAsJsonAsync(x.Url, new SubmitRsvp(x.Version, "ATTENDING", x.All)));
        await Created(await c.PostAsJsonAsync(a.Url, new SubmitRsvp(a.Version, "ATTENDING", a.All)));
        await Created(await c.PostAsJsonAsync(b.Url, new SubmitRsvp(b.Version, "ATTENDING", b.All)));
        var entries = (await owner.GetFromJsonAsync<PagedResult<WaitlistDto>>($"/api/weddings/{w}/rsvps/waitlist"))!.Items;
        Assert.Equal(new[] { a.GuestId, b.GuestId }, entries.Select(e => e.GuestId).ToArray()); // request order
        await Read<RsvpDto>(await c.PutAsJsonAsync(x.Url, new SubmitRsvp(rx.Version, "DECLINED", [])));

        using var second = await SignIn(factory, "owner", register: false);
        var races = await Task.WhenAll(owner.PostAsync($"/api/weddings/{w}/rsvps/waitlist/{entries[0].Id}/promote", null),
            second.PostAsync($"/api/weddings/{w}/rsvps/waitlist/{entries[1].Id}/promote", null));
        Assert.Single(races, r => r.StatusCode == HttpStatusCode.OK);
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "CAPACITY_FULL", Assert.Single(races, r => r.StatusCode != HttpStatusCode.OK));
        Assert.Equal(1, await Confirmed(db, w));

        var winner = (await db.WaitlistEntries.AsNoTracking().SingleAsync(e => e.Status == "PROMOTED")).GuestId == a.GuestId ? a : b;
        var ws = (await c.GetFromJsonAsync<RsvpDto>(winner.Url))!;
        await Read<RsvpDto>(await c.PutAsJsonAsync(winner.Url, new SubmitRsvp(ws.Version, "DECLINED", [])));
        var waitingId = (await db.WaitlistEntries.AsNoTracking().SingleAsync(e => e.Status == "WAITING")).Id;
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
        var w = await Wedding(db, ownerId, "rsvp-perm", 5); var a = await Party(db, w, ownerId, "A");
        var other = await Wedding(db, ownerId, "rsvp-perm-other", 5);
        using var viewer = await SignIn(factory, "viewer"); using var outsider = await SignIn(factory, "outsider");
        using var anonymous = factory.CreateHttpsClient();
        var viewerEmail = (await viewer.GetFromJsonAsync<AccountDto>("/api/auth/me"))!.Email;
        Assert.Equal(HttpStatusCode.Created, (await owner.PostAsJsonAsync($"/api/weddings/{w}/co-hosts", new AddCoHostInput(viewerEmail, ["RSVP_VIEW"]))).StatusCode);
        var url = $"/api/weddings/{w}/rsvps/{a.InvitationId}"; var body = new SubmitRsvp(a.Version, "ATTENDING", a.All);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/weddings/{w}/rsvps/waitlist")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync(url, body)).StatusCode); // needs GUEST_EDIT
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/weddings/{other}/rsvps/{a.InvitationId}")).StatusCode); // other wedding
        Assert.Equal(0, await db.Rsvps.CountAsync());
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
        foreach (var path in new[] { "/api/weddings/{weddingId}/rsvps/{invitationId}", "/api/weddings/{weddingId}/rsvps/waitlist", "/api/weddings/{weddingId}/rsvps/waitlist/{entryId}/promote" })
            Assert.True(paths.TryGetProperty(path, out _), path);
    });

    // ---------------- seeding & helpers ----------------

    private sealed record Seeded(Guid InvitationId, Guid GuestId, int Version, string Token, Guid Main, Guid[] Others)
    {
        public string Url => "/api/rsvp/" + Token;
        public Guid[] All => [Main, .. Others];
    }

    private static async Task<Guid> Wedding(InviteMeDbContext db, Guid ownerId, string slug, int capacity)
    {
        var wedding = Domain.Weddings.Wedding.Create(ownerId, "RSVP wedding", slug, capacity);
        var settings = WeddingSettings.Create(wedding.Id); settings.Configure("Asia/Ho_Chi_Minh", "PRIVATE", DateTimeOffset.UtcNow.AddDays(50), 2);
        db.Weddings.Add(wedding); db.WeddingSettings.Add(settings); db.WeddingEvents.Add(WeddingEvent.CreateMain(wedding.Id, DateTimeOffset.UtcNow.AddDays(60)));
        await db.SaveChangesAsync(); return wedding.Id;
    }

    private static async Task<Seeded> Party(InviteMeDbContext db, Guid weddingId, Guid ownerId, string code, int companions = 0, int maxPlusOne = 0)
    {
        var guest = WeddingGuest.Create(weddingId, code, "Guest " + code, null, null, "BRIDE", companions, maxPlusOne);
        var main = GuestParticipant.Create(guest.Id, "Guest " + code, "PRIMARY");
        var others = Enumerable.Range(1, companions).Select(i => GuestParticipant.Create(guest.Id, $"Companion {code}{i}", i == 1 ? "SPOUSE" : "CHILD")).ToList();
        var token = InvitationToken.Generate(); var hash = InvitationToken.Hash(token); var now = DateTimeOffset.UtcNow;
        var invitation = Invitation.Create(weddingId, guest.Id, hash, "{}");
        invitation.Preview(now); invitation.Review(now); invitation.Approve(ownerId, now); invitation.Publish(hash, "test-protected-token", now.AddDays(90), now);
        db.WeddingGuests.Add(guest); await db.SaveChangesAsync();
        db.GuestParticipants.Add(main); db.GuestParticipants.AddRange(others); db.Invitations.Add(invitation); await db.SaveChangesAsync();
        return new(invitation.Id, guest.Id, invitation.Version, token, main.Id, others.Select(o => o.Id).ToArray());
    }

    private static Task<int> Confirmed(InviteMeDbContext db, Guid weddingId) =>
        (from p in db.GuestParticipants join g in db.WeddingGuests on p.GuestId equals g.Id
         where g.WeddingId == weddingId && p.AttendanceStatus == "ATTENDING" select p).CountAsync();
    private static Task<List<RsvpHistory>> History(InviteMeDbContext db) =>
        db.RsvpHistory.AsNoTracking().OrderBy(x => x.ChangedAt).ThenBy(x => x.Id).ToListAsync();
    private static async Task<string> HistoryFingerprint(InviteMeDbContext db) => string.Join("\n",
        await db.Database.SqlQueryRaw<string>("SELECT row_to_json(h)::text AS \"Value\" FROM inviteme.rsvp_history h ORDER BY changed_at, id").ToListAsync());

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
}
