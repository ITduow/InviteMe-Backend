using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InviteMe.Application.Features.Guests.Import;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Invitations.Lifecycle;
using InviteMe.Application.Features.Rsvps.Workflow;
using InviteMe.Application.Features.Weddings.Workspace;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Domain.Rsvps;
using InviteMe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace InviteMe.IntegrationTests;

/// <summary>RSVP-02: submit/update through the real HTTP pipeline against real PostgreSQL, with append-only history.</summary>
public sealed class RsvpSubmitHistoryApiTests
{
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task FirstSubmitThenUpdatesAppendOneImmutableHistoryRowPerRealChange() => WithApi(async (db, factory, owner) =>
    {
        var w = await Wedding(owner, "rsvp02-happy", 10);
        var guest = await Family(owner, w);
        var pub = await Publish(owner, w, guest.Id);
        using var c = factory.CreateHttpsClient(); var url = Public(pub) + "/rsvp";
        var main = guest.Participants.Single(x => x.Type == "PRIMARY").Id;
        var spouse = guest.Participants.Single(x => x.Type != "PRIMARY").Id;

        var initial = (await c.GetFromJsonAsync<RsvpDto>(url))!;
        Assert.Equal("PENDING", initial.Status); Assert.Null(initial.RsvpId);

        // 1. First submission PENDING -> ATTENDING/1 : 201 + exactly one history row with no previous state.
        var created = await c.PostAsJsonAsync(url, new SubmitRsvp(initial.Version, "ATTENDING", [main]));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Null(created.Headers.Location);
        var first = (await created.Content.ReadFromJsonAsync<RsvpDto>())!;
        Assert.Equal("ATTENDING", first.Status); Assert.Equal(1, first.ConfirmedPartySize); Assert.NotNull(first.SubmittedAt);
        var rsvp = await db.Rsvps.AsNoTracking().SingleAsync();
        Assert.Equal(("ATTENDING", 1), (rsvp.Status, rsvp.ConfirmedPartySize));
        var h1 = Assert.Single(await History(db));
        Assert.Null(h1.OldStatus); Assert.Null(h1.OldConfirmedPartySize); Assert.Null(h1.OldSnapshot);
        Assert.Equal(("ATTENDING", 1, "RSVP_SUBMITTED"), (h1.NewStatus, h1.NewConfirmedPartySize, h1.Reason));
        Assert.Equal(("WEDDING_GUEST", guest.Id, (Guid?)null), (h1.ActorType, h1.ActorGuestId!.Value, h1.ChangedBy));
        using (var snapshot = JsonDocument.Parse(h1.NewSnapshot!))
        {
            Assert.Equal("ATTENDING", snapshot.RootElement.GetProperty("status").GetString());
            Assert.Equal(1, snapshot.RootElement.GetProperty("confirmedPartySize").GetInt32());
            Assert.Equal(2, snapshot.RootElement.GetProperty("participants").GetArrayLength());
            Assert.DoesNotContain(pub.PublicPath.Split('/').Last(), h1.NewSnapshot);
        }
        var frozen = await HistoryFingerprint(db);

        // 6. Duplicate first submission -> 409 RSVP_ALREADY_EXISTS, nothing written.
        await AssertProblem(HttpStatusCode.Conflict, "RSVP_ALREADY_EXISTS", await c.PostAsJsonAsync(url, new SubmitRsvp(first.Version, "DECLINED", [])));
        Assert.Equal(frozen, await HistoryFingerprint(db));

        // 3. Party size ATTENDING 1 -> ATTENDING 2 (spouse is an invited participant, within entitlement).
        var grown = await Read<RsvpDto>(await c.PutAsJsonAsync(url, new SubmitRsvp(first.Version, "ATTENDING", [main, spouse])));
        Assert.Equal(2, grown.ConfirmedPartySize); Assert.Equal(first.SubmittedAt, grown.SubmittedAt);
        var afterGrow = await History(db);
        Assert.Equal(2, afterGrow.Count);
        Assert.Equal(("ATTENDING", 1, "ATTENDING", 2, "RSVP_UPDATED"),
            (afterGrow[1].OldStatus, afterGrow[1].OldConfirmedPartySize!.Value, afterGrow[1].NewStatus, afterGrow[1].NewConfirmedPartySize, afterGrow[1].Reason));
        Assert.Equal(Normalize(afterGrow[0].NewSnapshot!), Normalize(afterGrow[1].OldSnapshot!));
        Assert.StartsWith(frozen, await HistoryFingerprint(db));

        // No-op: identical representation -> 200, no history row, no version bump, updated_at unchanged.
        var updatedAt = (await db.Rsvps.AsNoTracking().SingleAsync()).UpdatedAt;
        var same = await Read<RsvpDto>(await c.PutAsJsonAsync(url, new SubmitRsvp(grown.Version, "ATTENDING", [spouse, main])));
        Assert.Equal(grown.Version, same.Version);
        Assert.Equal(2, await db.RsvpHistory.CountAsync());
        Assert.Equal(updatedAt, (await db.Rsvps.AsNoTracking().SingleAsync()).UpdatedAt);

        // 2. ATTENDING -> DECLINED: current row updated, older history preserved byte-for-byte.
        var frozen2 = await HistoryFingerprint(db);
        var declined = await Read<RsvpDto>(await c.PutAsJsonAsync(url, new SubmitRsvp(same.Version, "DECLINED", [])));
        Assert.Equal(("DECLINED", 0), (declined.Status, declined.ConfirmedPartySize));
        Assert.Equal(("DECLINED", 0), ((await db.Rsvps.AsNoTracking().SingleAsync()).Status, (await db.Rsvps.AsNoTracking().SingleAsync()).ConfirmedPartySize));
        var all = await History(db);
        Assert.Equal(3, all.Count);
        Assert.Equal(("ATTENDING", 2, "DECLINED", 0), (all[2].OldStatus, all[2].OldConfirmedPartySize!.Value, all[2].NewStatus, all[2].NewConfirmedPartySize));
        Assert.StartsWith(frozen2, await HistoryFingerprint(db));

        // Stale expectedVersion -> 409, nothing written.
        await AssertProblem(HttpStatusCode.Conflict, "CONCURRENCY_CONFLICT", await c.PutAsJsonAsync(url, new SubmitRsvp(grown.Version, "ATTENDING", [main])));
        Assert.Equal(3, await db.RsvpHistory.CountAsync());

        // Host/Co-host management route records a USER actor.
        await Read<RsvpDto>(await owner.PutAsJsonAsync(Root(w) + $"/invitations/{pub.Invitation.Id}/rsvp", new SubmitRsvp(declined.Version, "ATTENDING", [main])));
        var hostRow = (await History(db)).Last();
        Assert.Equal("USER", hostRow.ActorType); Assert.NotNull(hostRow.ChangedBy); Assert.Null(hostRow.ActorGuestId);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task InvalidRequestsAreRejectedWithoutWritingRsvpOrHistory() => WithApi(async (db, factory, owner) =>
    {
        var w = await Wedding(owner, "rsvp02-negative", 10);
        var guest = await Family(owner, w); var pub = await Publish(owner, w, guest.Id);
        using var c = factory.CreateHttpsClient(); var url = Public(pub) + "/rsvp";
        var main = guest.Participants.Single(x => x.Type == "PRIMARY").Id; var v = pub.Invitation.Version;

        // 4. Status/party-size consistency -> 400.
        foreach (var body in new object[]
                 {
                     new SubmitRsvp(v, "DECLINED", [main]), new SubmitRsvp(v, "ATTENDING", []), new SubmitRsvp(v, "MAYBE", [main]),
                     new SubmitRsvp(v, "PENDING", []), new { expectedVersion = v, decision = "ATTENDING", participantIds = new[] { main }, unknown = 1 }
                 })
            Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync(url, body)).StatusCode);

        // 5. Unknown / malformed invitation tokens -> 404 without revealing anything.
        var unknown = "/api/public/invitations/" + new string('a', 64) + "/rsvp";
        await AssertProblem(HttpStatusCode.NotFound, "INVITATION_NOT_FOUND", await c.GetAsync(unknown));
        await AssertProblem(HttpStatusCode.NotFound, "INVITATION_NOT_FOUND", await c.PostAsJsonAsync(unknown, new SubmitRsvp(1, "ATTENDING", [main])));
        await AssertProblem(HttpStatusCode.NotFound, "INVITATION_NOT_FOUND", await c.PutAsJsonAsync("/api/public/invitations/not-a-token/rsvp", new SubmitRsvp(1, "DECLINED", [])));

        // Update before first submission -> 404 RSVP_NOT_FOUND.
        await AssertProblem(HttpStatusCode.NotFound, "RSVP_NOT_FOUND", await c.PutAsJsonAsync(url, new SubmitRsvp(v, "DECLINED", [])));
        // Claiming more people than invited (no plus-one allowance on this guest) -> 422.
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "PLUS_ONE_LIMIT", await c.PostAsJsonAsync(url, new SubmitRsvp(v, "ATTENDING", [main], ["Extra", "Extra 2"])));
        Assert.Equal(0, await db.Rsvps.CountAsync()); Assert.Equal(0, await db.RsvpHistory.CountAsync());

        // 7. Deadline read from wedding_settings.rsvp_deadline (RSVP-01 §5): after it, increases / re-attendance are rejected
        //    (repository convention: business rule → 422) without touching history; a late cancellation is still accepted.
        var spouse = guest.Participants.Single(x => x.Type != "PRIMARY").Id;
        var first = await Read<RsvpDto>(await c.PostAsJsonAsync(url, new SubmitRsvp(v, "ATTENDING", [main])));
        var frozen = await HistoryFingerprint(db);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inviteme.wedding_settings SET rsvp_deadline={DateTimeOffset.UtcNow.AddMinutes(-1)} WHERE wedding_id={w.Id}");
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "RSVP_DEADLINE_PASSED", await c.PutAsJsonAsync(url, new SubmitRsvp(first.Version, "ATTENDING", [main, spouse])));
        Assert.Equal(("ATTENDING", 1), ((await db.Rsvps.AsNoTracking().SingleAsync()).Status, (await db.Rsvps.AsNoTracking().SingleAsync()).ConfirmedPartySize));
        Assert.Equal(frozen, await HistoryFingerprint(db));
        var cancelled = await Read<RsvpDto>(await c.PutAsJsonAsync(url, new SubmitRsvp(first.Version, "DECLINED", [])));
        Assert.Equal("DECLINED", cancelled.Status);
        Assert.Equal("RSVP_LATE_CANCELLATION", (await History(db)).Last().Reason);
        Assert.StartsWith(frozen, await HistoryFingerprint(db));
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "RSVP_DEADLINE_PASSED", await c.PutAsJsonAsync(url, new SubmitRsvp(cancelled.Version, "ATTENDING", [main])));
        Assert.Equal(2, await db.RsvpHistory.CountAsync());
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task HistoryIsAppendOnlyInDatabaseAndNotExposedForMutation() => WithApi(async (db, factory, owner) =>
    {
        var w = await Wedding(owner, "rsvp02-immutable", 10);
        var guest = await Family(owner, w); var pub = await Publish(owner, w, guest.Id);
        using var c = factory.CreateHttpsClient();
        await Read<RsvpDto>(await c.PostAsJsonAsync(Public(pub) + "/rsvp", new SubmitRsvp(pub.Invitation.Version, "DECLINED", [])));
        var frozen = await HistoryFingerprint(db);

        foreach (var sql in new[] { "UPDATE inviteme.rsvp_history SET reason = 'tampered'", "DELETE FROM inviteme.rsvp_history", "TRUNCATE inviteme.rsvp_history", "DELETE FROM inviteme.rsvps" })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql));
            Assert.True(error.SqlState is PostgresErrorCodes.RestrictViolation or PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        }
        Assert.Equal(frozen, await HistoryFingerprint(db));

        // 8. No route can update or delete history.
        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => (Pattern: e.RoutePattern.RawText ?? "", Methods: e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []))
            .ToList();
        Assert.DoesNotContain(routes, r => r.Pattern.Contains("history", StringComparison.OrdinalIgnoreCase) && r.Pattern.Contains("rsvp", StringComparison.OrdinalIgnoreCase)
            && r.Methods.Any(m => m is "PUT" or "PATCH" or "DELETE" or "POST"));
        Assert.Contains(routes, r => r.Pattern == "/api/public/invitations/{token}/rsvp" && r.Methods.Contains("PUT"));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task FailedHistoryInsertRollsBackTheRsvpChange() => WithApi(async (db, factory, owner) =>
    {
        var w = await Wedding(owner, "rsvp02-rollback", 10);
        var guest = await Family(owner, w); var pub = await Publish(owner, w, guest.Id);
        using var c = factory.CreateHttpsClient(); var url = Public(pub) + "/rsvp";
        var main = guest.Participants.Single(x => x.Type == "PRIMARY").Id;
        var first = await Read<RsvpDto>(await c.PostAsJsonAsync(url, new SubmitRsvp(pub.Invitation.Version, "ATTENDING", [main])));
        var frozen = await HistoryFingerprint(db);

        // 9. Make the history INSERT fail inside the same transaction as the RSVP update.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION inviteme.test_fail_history() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'simulated history failure'; END; $$;
            CREATE TRIGGER trg_test_fail_history BEFORE INSERT ON inviteme.rsvp_history FOR EACH ROW EXECUTE FUNCTION inviteme.test_fail_history();
            """);
        var failed = await c.PutAsJsonAsync(url, new SubmitRsvp(first.Version, "DECLINED", []));
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.DoesNotContain("simulated", await failed.Content.ReadAsStringAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER trg_test_fail_history ON inviteme.rsvp_history; DROP FUNCTION inviteme.test_fail_history();");

        var rsvp = await db.Rsvps.AsNoTracking().SingleAsync();
        Assert.Equal(("ATTENDING", 1), (rsvp.Status, rsvp.ConfirmedPartySize));
        Assert.Equal(first.Version, (await c.GetFromJsonAsync<RsvpDto>(url))!.Version);
        Assert.Equal(frozen, await HistoryFingerprint(db));
        Assert.True(await db.GuestParticipants.AsNoTracking().AnyAsync(x => x.Id == main && x.AttendanceStatus == "ATTENDING"));
    });

    private static Task<List<RsvpHistory>> History(InviteMeDbContext db) =>
        db.RsvpHistory.AsNoTracking().OrderBy(x => x.ChangedAt).ThenBy(x => x.Id).ToListAsync();
    // Every stored column of every history row, in insertion order; used to prove earlier rows never change.
    private static async Task<string> HistoryFingerprint(InviteMeDbContext db) => string.Join("\n",
        await db.Database.SqlQueryRaw<string>("SELECT row_to_json(h)::text AS \"Value\" FROM inviteme.rsvp_history h ORDER BY changed_at, id").ToListAsync());
    private static string Normalize(string json) => JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement);

    private static Task WithApi(Func<InviteMeDbContext, WorkspaceApiFactory, HttpClient, Task> test) => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync(); await using var factory = new WorkspaceApiFactory(db.Database.GetConnectionString()!);
        using var owner = await SignIn(factory, "owner"); await test(db, factory, owner);
    });
    private static WorkspaceInput Settings(string slug, int capacity) => new("RSVP-02 wedding", slug, capacity, "Asia/Ho_Chi_Minh",
        DateTimeOffset.UtcNow.AddDays(60), DateTimeOffset.UtcNow.AddDays(60).AddHours(3), DateTimeOffset.UtcNow.AddDays(50), "Venue", "Address");
    private static async Task<WorkspaceDto> Wedding(HttpClient c, string slug, int capacity) => await Read<WorkspaceDto>(await c.PostAsJsonAsync("/api/weddings", Settings(slug, capacity)));
    private static string Root(WorkspaceDto w) => $"/api/weddings/{w.Id}";
    private static string Public(PublicationDto p) => "/api/public/invitations/" + p.PublicPath.Split('/').Last();
    private static async Task<ImportedGuestDto> Family(HttpClient c, WorkspaceDto w) => Assert.Single((await Read<ImportResult>(await c.PostAsJsonAsync(Root(w) + "/guests/import",
        new ImportGuests([new("F001", "Family", Email: "family@example.test", Companions: [new("Spouse", "SPOUSE")])])))).Guests);
    private static async Task<PublicationDto> Publish(HttpClient c, WorkspaceDto w, Guid guest)
    {
        var template = (await c.GetFromJsonAsync<List<TemplateDto>>("/api/invitation-templates"))!.Single(x => x.Slug == "inviteme-classic");
        var x = await Read<InvitationDto>(await c.PostAsJsonAsync(Root(w) + "/invitations", new CreateInvitation(guest, new(template.Id, "One", "Two", "Welcome"))));
        foreach (var action in new[] { "preview", "review", "approve" }) x = await Read<InvitationDto>(await c.PostAsJsonAsync(Root(w) + $"/invitations/{x.Id}/{action}", new InvitationAction(x.Version)));
        return await Read<PublicationDto>(await c.PostAsJsonAsync(Root(w) + $"/invitations/{x.Id}/publish", new InvitationAction(x.Version, DateTimeOffset.UtcNow.AddDays(90))));
    }
    private static async Task<T> Read<T>(HttpResponseMessage response)
    { Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}"); return (await response.Content.ReadFromJsonAsync<T>())!; }
    private static async Task AssertProblem(HttpStatusCode status, string code, HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"{response.StatusCode}: {body}");
        Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }
    private static async Task<HttpClient> SignIn(WorkspaceApiFactory factory, string name)
    {
        var c = factory.CreateHttpsClient(); const string password = "Test-only-Pass123!";
        await Read<AccountDto>(await c.PostAsJsonAsync("/api/auth/register", new RegisterAccount(name + "@example.test", password, name)));
        var token = await Read<TokenDto>(await c.PostAsJsonAsync("/api/auth/login", new LoginAccount(name + "@example.test", password)));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken); return c;
    }
}
