using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Guests.Import;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Invitations.Lifecycle;
using InviteMe.Application.Features.Reception;
using InviteMe.Application.Features.Rsvps.Workflow;
using InviteMe.Application.Features.Seating.Workflow;
using InviteMe.Application.Features.Weddings.Workspace;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Realtime;
using InviteMe.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace InviteMe.IntegrationTests;

// SEAT-02/03/04 and CHK-02/03 on top of the PR #4 seating/reception workflow, through real HTTP.
public sealed class SeatingCheckInApiTests
{
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task TableLifecycleEnforcesVenueLimitsReasonsAndHistory() => WithApi(async (db, factory, owner) =>
    {
        var root = Root(await Wedding(owner, "lifecycle", 20));
        var settings = await Read<SeatingSettingsDto>(await owner.PutAsJsonAsync(root + "/seating/settings", new SeatingSettings(1, 1, 4)));
        Assert.Equal((1, 1, (short)4), (settings.BookedTableCount!.Value, settings.BackupTableLimit, settings.DefaultTableCapacity));

        var primary = await Read<TableDto>(await owner.PostAsJsonAsync(root + "/tables", new CreateTable("1", 4, "ACTIVE")));
        Assert.Equal(("PRIMARY", "EMPTY"), (primary.Kind, primary.FillLevel));
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "BOOKED_TABLE_LIMIT_REACHED", await owner.PostAsJsonAsync(root + "/tables", new CreateTable("2", 4)));
        await AssertProblem(HttpStatusCode.BadRequest, "VALIDATION_FAILED", await owner.PostAsJsonAsync(root + "/tables", new CreateTable("X", 4, "BACKUP", "PRIMARY")));

        var backup = await Read<TableDto>(await owner.PostAsJsonAsync(root + "/tables", new CreateTable("B1", 4, "BACKUP")));
        Assert.Equal(("BACKUP", "BACKUP"), (backup.Kind, backup.Status));
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "BACKUP_LIMIT_REACHED", await owner.PostAsJsonAsync(root + "/tables", new CreateTable("B2", 4, "BACKUP")));
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "TABLE_INVALID_TRANSITION",
            await owner.PutAsJsonAsync(root + $"/tables/{primary.Id}", new UpdateTable(primary.Version, 4, "BACKUP")));
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "OVERFLOW_REASON_REQUIRED",
            await owner.PutAsJsonAsync(root + $"/tables/{backup.Id}", new UpdateTable(backup.Version, 4, "ACTIVE")));
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "OVERFLOW_REASON_REQUIRED",
            await owner.PutAsJsonAsync(root + $"/tables/{backup.Id}", new UpdateTable(backup.Version, 4, "ACTIVE", "OTHER")));
        var activated = await Read<TableDto>(await owner.PutAsJsonAsync(root + $"/tables/{backup.Id}",
            new UpdateTable(backup.Version, 4, "ACTIVE", "WALK_IN", "Venue confirmed at 17:40")));
        Assert.Equal("ACTIVE", activated.Status); Assert.NotNull(activated.ActivatedAt);

        var history = (await owner.GetFromJsonAsync<List<TableHistoryDto>>(root + $"/tables/{backup.Id}/history"))!;
        Assert.Equal(new string?[] { null, "BACKUP" }, history.Select(x => x.FromStatus));
        Assert.Equal("WALK_IN", history[1].ReasonCode);
        Assert.Contains("\"demand\"", history[1].OverflowSnapshot!, StringComparison.Ordinal);
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "TABLE_LIMIT_BELOW_USAGE", await owner.PutAsJsonAsync(root + "/seating/settings", new SeatingSettings(1, 0, 4)));

        var overflow = (await owner.GetFromJsonAsync<OverflowDto>(root + "/seating/overflow"))!;
        Assert.Equal((0, 8, 0), (overflow.Demand, overflow.Supply, overflow.BackupTablesRemaining));
        Assert.Contains(factory.Changes, x => x.Type == "TableUpdated" && x.EntityId == backup.Id);
        Assert.Equal(1, await db.TableStatusHistory.CountAsync(x => x.ReasonCode == "WALK_IN"));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task QrLinkScanDuplicateVoidAndManualLookup() => WithApi(async (db, factory, owner) =>
    {
        var w = await Wedding(owner, "reception", 20); var root = Root(w);
        var guest = await Read<ImportedGuestDto>(await owner.PostAsJsonAsync(root + "/guests", new GuestInput("G001", "Nguyễn Văn C", Phone: "0912 345 678",
            Side: "GROOM", MaxPlusOne: 1, Companions: [new("Trần Thị D", "SPOUSE")])));
        var pub = await Publish(owner, w, guest.Id); await Attend(owner, pub, guest);
        var table = await Read<TableDto>(await owner.PostAsJsonAsync(root + "/tables", new CreateTable("5", 10, "ACTIVE")));
        var c = guest.Participants[0].Id; var spouse = guest.Participants[1].Id;
        await Read<AssignmentDto>(await owner.PutAsJsonAsync(root + $"/seating/{c}", new AssignSeat(table.Id, null, table.Version)));

        // The QR carries the whole invitation link, not only the bare token.
        var scan = await Read<ScanResult>(await owner.PostAsJsonAsync(root + "/check-ins/scan", new ScanInvitation("https://inviteme.app" + pub.PublicPath)));
        Assert.Equal(guest.Id, scan.GuestId);
        await AssertProblem(HttpStatusCode.BadRequest, "VALIDATION_FAILED", await owner.PostAsJsonAsync(root + "/check-ins/scan", new ScanInvitation("hello")));

        var first = await Read<CheckInDto>(await owner.PostAsJsonAsync(root + "/check-ins", new CheckInInput(c)));
        var again = await Read<CheckInDto>(await owner.PostAsJsonAsync(root + "/check-ins", new CheckInInput(c, Method: "MANUAL")));
        Assert.Equal((first.Id, false, true, "owner"), (again.Id, first.AlreadyCheckedIn, again.AlreadyCheckedIn, again.CheckedInByName));

        var mistaken = await Read<CheckInDto>(await owner.PostAsJsonAsync(root + "/check-ins", new CheckInInput(spouse, Method: "MANUAL")));
        Assert.Equal("MANUAL", mistaken.Method);
        Assert.Equal("VOID", (await Read<CheckInDto>(await owner.PostAsJsonAsync(root + $"/check-ins/{mistaken.Id}/void", new VoidCheckIn("Wrong person")))).Status);
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "CHECKIN_ALREADY_VOID", await owner.PostAsJsonAsync(root + $"/check-ins/{mistaken.Id}/void", new VoidCheckIn("again")));
        var retried = await Read<CheckInDto>(await owner.PostAsJsonAsync(root + "/check-ins", new CheckInInput(spouse)));
        Assert.NotEqual(mistaken.Id, retried.Id);
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "PARTICIPANT_CHECKED_IN",
            await owner.PostAsJsonAsync(root + $"/seating/{c}/unassign", new UnassignSeat(1, table.Version + 1)));

        foreach (var query in new[] { "nguyen van c", "tran thi", "5678", "g001" })
        {
            var found = Assert.Single((await Read<List<GuestSearchDto>>(await owner.PostAsJsonAsync(root + "/check-ins/search", new SearchGuests(query))))!);
            Assert.Equal((guest.Id, "*******678", 2), (found.GuestId, found.PhoneMasked, found.CheckedInCount));
        }
        Assert.Empty((await Read<List<GuestSearchDto>>(await owner.PostAsJsonAsync(root + "/check-ins/search", new SearchGuests("100%"))))!);

        var party = (await owner.GetFromJsonAsync<PartyDto>(root + $"/check-ins/parties/{guest.Id}"))!;
        Assert.Equal(("5", "owner"), (party.Members[0].Table!.TableNumber, party.Members[0].CheckIn!.CheckedInByName));
        Assert.Null(party.Members[1].Table);
        var summary = (await owner.GetFromJsonAsync<CheckInSummaryDto>(root + "/check-ins/summary"))!;
        Assert.Equal((2, 2, 1, 0), (summary.ExpectedAttendees, summary.CheckedInAttendees, summary.SeatedAttendees, summary.NotArrived));
        Assert.Equal(2, await db.CheckIns.CountAsync(x => x.ParticipantId == spouse));
        Assert.Contains(factory.Changes, x => x.Type == "CheckInVoided" && x.EntityId == mistaken.Id);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task WalkInsShareTableCapacityWithSeatedGuests() => WithApi(async (db, factory, owner) =>
    {
        var w = await Wedding(owner, "walkins", 20); var root = Root(w);
        var guest = await Read<ImportedGuestDto>(await owner.PostAsJsonAsync(root + "/guests", new GuestInput("G001", "Family", Email: "family@example.test",
            MaxPlusOne: 1, Companions: [new("Spouse", "SPOUSE")])));
        var pub = await Publish(owner, w, guest.Id); await Attend(owner, pub, guest);
        var table = await Read<TableDto>(await owner.PostAsJsonAsync(root + "/tables", new CreateTable("1", 2, "ACTIVE")));
        var planned = await Read<TableDto>(await owner.PostAsJsonAsync(root + "/tables", new CreateTable("2", 2)));
        await Read<AssignmentDto>(await owner.PutAsJsonAsync(root + $"/seating/{guest.Participants[0].Id}", new AssignSeat(table.Id, null, table.Version)));
        table = (await owner.GetFromJsonAsync<PagedResult<TableDto>>(root + "/tables"))!.Items.Single(x => x.Id == table.Id);

        await AssertProblem(HttpStatusCode.UnprocessableEntity, "TABLE_FULL", await owner.PostAsJsonAsync(root + "/walk-ins", new WalkinInput("Khách vãng lai", 2, TableId: table.Id)));
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "TABLE_NOT_ACTIVE", await owner.PostAsJsonAsync(root + "/walk-ins", new WalkinInput("Khách", 1, TableId: planned.Id)));
        await AssertProblem(HttpStatusCode.NotFound, "RELATED_GUEST_NOT_FOUND", await owner.PostAsJsonAsync(root + "/walk-ins", new WalkinInput("Khách", 1, RelatedGuestId: Guid.NewGuid())));
        var walk = await Read<WalkinDto>(await owner.PostAsJsonAsync(root + "/walk-ins", new WalkinInput("Bạn của Family", 1, Side: "BRIDE", RelatedGuestId: guest.Id, TableId: table.Id)));
        Assert.Equal((table.Id, guest.Id, "BRIDE"), (walk.TableId!.Value, walk.RelatedGuestId!.Value, walk.Side));
        Assert.Equal("WALK_IN", (await Read<CheckInDto>(await owner.PostAsJsonAsync(root + "/check-ins", new CheckInInput(WalkinId: walk.Id)))).Method);

        // One guest plus the walk-in fill the 2-seat table, so the spouse cannot sit there.
        table = (await owner.GetFromJsonAsync<PagedResult<TableDto>>(root + "/tables"))!.Items.Single(x => x.Id == table.Id);
        Assert.Equal((2, "FULL"), ((int)table.Occupied, table.FillLevel));
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "TABLE_FULL",
            await owner.PutAsJsonAsync(root + $"/seating/{guest.Participants[1].Id}", new AssignSeat(table.Id, null, table.Version)));
        Assert.Null((await Read<WalkinDto>(await owner.PutAsJsonAsync(root + $"/walk-ins/{walk.Id}/table", new AssignWalkinTable(null)))).TableId);
        await Read<AssignmentDto>(await owner.PutAsJsonAsync(root + $"/seating/{guest.Participants[1].Id}", new AssignSeat(table.Id, null, table.Version)));

        var overflow = (await owner.GetFromJsonAsync<OverflowDto>(root + "/seating/overflow"))!;
        Assert.Equal((3, 1, 1), (overflow.Demand, overflow.Breakdown.WalkIn, overflow.Unseated));
        var unseated = (await owner.GetFromJsonAsync<List<UnseatedDto>>(root + "/seating/unseated?side=MUTUAL"))!;
        Assert.Empty(unseated);
        var summary = (await owner.GetFromJsonAsync<CheckInSummaryDto>(root + "/check-ins/summary"))!;
        Assert.Equal((1, 1), (summary.WalkInGroups, summary.WalkInPeople));
        Assert.Equal(1, await db.WalkIns.CountAsync(x => x.RelatedGuestId == guest.Id));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task DeclinedArrivalIsAdmittedByOverrideWithoutRewritingRsvp() => WithApi(async (db, factory, owner) =>
    {
        var w = await Wedding(owner, "override", 20); var root = Root(w);
        var guest = await Read<ImportedGuestDto>(await owner.PostAsJsonAsync(root + "/guests", new GuestInput("G001", "Family", Email: "family@example.test",
            MaxPlusOne: 1, Companions: [new("Spouse", "SPOUSE")])));
        var pub = await Publish(owner, w, guest.Id);
        // Only the primary guest attends; the spouse is recorded as DECLINED.
        await Read<RsvpDto>(await owner.PutAsJsonAsync(root + $"/invitations/{pub.Invitation.Id}/rsvp",
            new SubmitRsvp(pub.Invitation.Version, "ATTENDING", [guest.Participants[0].Id])));
        var spouse = guest.Participants[1].Id;
        var historyBefore = await db.RsvpHistory.CountAsync();

        await AssertProblem(HttpStatusCode.UnprocessableEntity, "PARTICIPANT_NOT_ATTENDING", await owner.PostAsJsonAsync(root + "/check-ins", new CheckInInput(spouse)));
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "OVERRIDE_REASON_MISMATCH",
            await owner.PostAsJsonAsync(root + "/check-ins", new CheckInInput(spouse, OverrideReason: "PENDING_ARRIVED")));
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "OVERRIDE_NOT_APPLICABLE",
            await owner.PostAsJsonAsync(root + "/check-ins", new CheckInInput(guest.Participants[0].Id, OverrideReason: "DECLINED_ARRIVED")));
        var admitted = await Read<CheckInDto>(await owner.PostAsJsonAsync(root + "/check-ins", new CheckInInput(spouse, Method: "MANUAL", OverrideReason: "DECLINED_ARRIVED")));
        Assert.Equal(("DECLINED_ARRIVED", "MANUAL"), (admitted.OverrideReason, admitted.Method));

        Assert.Equal("DECLINED", (await db.GuestParticipants.AsNoTracking().SingleAsync(x => x.Id == spouse)).AttendanceStatus);
        Assert.Equal(historyBefore, await db.RsvpHistory.CountAsync());
        Assert.Equal(1, await db.AuditLogs.CountAsync(x => x.Action == "CHECKED_IN_OVERRIDE"));
        var summary = (await owner.GetFromJsonAsync<CheckInSummaryDto>(root + "/check-ins/summary"))!;
        Assert.Equal((1, 0, 1), (summary.ExpectedAttendees, summary.CheckedInAttendees, summary.CheckedInOthers));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task DecliningReleasesTheSeatButCheckedInAttendanceStaysLocked() => WithApi(async (db, factory, owner) =>
    {
        var w = await Wedding(owner, "decline", 20); var root = Root(w);
        var guest = await Read<ImportedGuestDto>(await owner.PostAsJsonAsync(root + "/guests", new GuestInput("G001", "Family", Email: "family@example.test",
            MaxPlusOne: 1, Companions: [new("Spouse", "SPOUSE")])));
        var pub = await Publish(owner, w, guest.Id); await Attend(owner, pub, guest);
        var table = await Read<TableDto>(await owner.PostAsJsonAsync(root + "/tables", new CreateTable("5", 10, "ACTIVE")));
        foreach (var p in guest.Participants)
        {
            await Read<AssignmentDto>(await owner.PutAsJsonAsync(root + $"/seating/{p.Id}", new AssignSeat(table.Id, null, table.Version)));
            table = (await owner.GetFromJsonAsync<PagedResult<TableDto>>(root + "/tables"))!.Items.Single();
        }
        var version = (await owner.GetFromJsonAsync<RsvpDto>(root + $"/invitations/{pub.Invitation.Id}/rsvp"))!.Version;

        // The spouse cancels: the RSVP change is accepted and the seat is released (Table 5 now underfilled).
        var reduced = await Read<RsvpDto>(await owner.PutAsJsonAsync(root + $"/invitations/{pub.Invitation.Id}/rsvp",
            new SubmitRsvp(version, "ATTENDING", [guest.Participants[0].Id])));
        Assert.Equal(1, reduced.ConfirmedPartySize);
        table = (await owner.GetFromJsonAsync<PagedResult<TableDto>>(root + "/tables"))!.Items.Single();
        Assert.Equal((1, "UNDERFILLED"), ((int)table.Occupied, table.FillLevel));
        Assert.Equal(1, await db.SeatingChangeLogs.CountAsync(x => x.ParticipantId == guest.Participants[1].Id && x.Action == "UNASSIGN"));
        Assert.Equal(1, await db.AuditLogs.CountAsync(x => x.Action == "SEAT_RELEASED_BY_RSVP"));

        // Once the primary guest is checked in, declining cannot erase that attendance.
        await Read<CheckInDto>(await owner.PostAsJsonAsync(root + "/check-ins", new CheckInInput(guest.Participants[0].Id)));
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "ATTENDANCE_LOCKED", await owner.PutAsJsonAsync(root + $"/invitations/{pub.Invitation.Id}/rsvp",
            new SubmitRsvp(reduced.Version, "DECLINED", [])));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ArrivalsBeyondCapacityWarnByDefaultAndBlockWhenTheWeddingOptsIn() => WithApi(async (db, factory, owner) =>
    {
        var w = await Wedding(owner, "arrivals", 2); var root = Root(w);
        var guest = await Read<ImportedGuestDto>(await owner.PostAsJsonAsync(root + "/guests", new GuestInput("G001", "Family", Email: "family@example.test",
            MaxPlusOne: 1, Companions: [new("Spouse", "SPOUSE")])));
        var pub = await Publish(owner, w, guest.Id); await Attend(owner, pub, guest);

        var first = await Read<WalkinDto>(await owner.PostAsJsonAsync(root + "/walk-ins", new WalkinInput("Khách vãng lai", 1)));
        var admitted = await Read<CheckInDto>(await owner.PostAsJsonAsync(root + "/check-ins", new CheckInInput(WalkinId: first.Id)));
        Assert.True(admitted.OverCapacity);

        var settings = await Read<SeatingSettingsDto>(await owner.PutAsJsonAsync(root + "/seating/settings", new SeatingSettings(null, 0, 10, true)));
        Assert.True(settings.BlockArrivalsOverCapacity);
        var second = await Read<WalkinDto>(await owner.PostAsJsonAsync(root + "/walk-ins", new WalkinInput("Khách thứ hai", 1)));
        await AssertProblem(HttpStatusCode.UnprocessableEntity, "CAPACITY_FULL", await owner.PostAsJsonAsync(root + "/check-ins", new CheckInInput(WalkinId: second.Id)));
        Assert.Equal(1, await db.CheckIns.CountAsync(x => x.WalkinId != null));
    });

    private static Task WithApi(Func<InviteMeDbContext, RecordingApiFactory, HttpClient, Task> test) => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync(); await using var factory = new RecordingApiFactory(db.Database.GetConnectionString()!);
        using var owner = await SignIn(factory, "owner"); await test(db, factory, owner);
    });
    private static WorkspaceInput Settings(string slug, int capacity) => new("Seating wedding", slug, capacity, "Asia/Ho_Chi_Minh",
        DateTimeOffset.UtcNow.AddDays(60), DateTimeOffset.UtcNow.AddDays(60).AddHours(3), DateTimeOffset.UtcNow.AddDays(50), "Venue", "Address");
    private static async Task<WorkspaceDto> Wedding(HttpClient c, string slug, int capacity) => await Read<WorkspaceDto>(await c.PostAsJsonAsync("/api/weddings", Settings(slug, capacity)));
    private static string Root(WorkspaceDto w) => $"/api/weddings/{w.Id}";
    private static async Task Attend(HttpClient c, PublicationDto p, ImportedGuestDto g) =>
        await Read<RsvpDto>(await c.PutAsJsonAsync($"/api/weddings/{p.Invitation.WeddingId}/invitations/{p.Invitation.Id}/rsvp",
            new SubmitRsvp(p.Invitation.Version, "ATTENDING", g.Participants.Select(x => x.Id).ToArray())));
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
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}");
        Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }
    private static async Task<HttpClient> SignIn(RecordingApiFactory factory, string name)
    {
        var c = factory.CreateHttpsClient(); const string password = "Test-only-Pass123!";
        await Read<AccountDto>(await c.PostAsJsonAsync("/api/auth/register", new RegisterAccount(name + "@example.test", password, name)));
        var token = await Read<TokenDto>(await c.PostAsJsonAsync("/api/auth/login", new LoginAccount(name + "@example.test", password)));
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken); return c;
    }

    // Same host configuration as WorkspaceApiFactory, plus a recording realtime publisher.
    internal sealed class RecordingApiFactory(string connection) : WebApplicationFactory<Program>
    {
        public ConcurrentQueue<WeddingChange> Changes { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:PostgreSQL"] = connection, ["Jwt:Issuer"] = "InviteMe.Tests", ["Jwt:Audience"] = "InviteMe.Tests.Web",
                ["Jwt:SigningKey"] = Convert.ToBase64String(ApiFactory.SigningKey), ["Frontend:Url"] = "http://localhost:3000", ["Invitations:DispatchEnabled"] = "false"
            }));
            builder.ConfigureTestServices(services => services.AddScoped<IWeddingChangePublisher>(_ => new Recorder(Changes)));
        }
        public HttpClient CreateHttpsClient() => CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
    }

    private sealed class Recorder(ConcurrentQueue<WeddingChange> changes) : IWeddingChangePublisher
    {
        public Task PublishAsync(Guid weddingId, WeddingChange change, CancellationToken cancellationToken) { changes.Enqueue(change); return Task.CompletedTask; }
    }
}
