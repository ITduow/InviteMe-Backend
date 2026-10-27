using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InviteMe.Application.Features.Guests.Access;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Guests.Reception;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Rsvps;
using InviteMe.Domain.CheckIn;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.IntegrationTests;

// Cohost04 walk-ins (capacity, idempotency, audit) and undoing a check-in, through real HTTP.
public sealed class ReceptionWalkInApiTests
{
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task WalkInsUseCapacityAndReplayAndCheckInsCanBeVoided() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        await using var factory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
        using var owner = await CoHostPermissionTests.SignIn(factory, "walkin-owner");
        var account = (await owner.GetFromJsonAsync<AccountDto>("/api/auth/me"))!;
        var wedding = Wedding.Create(account.Id, "Walk-in Wedding", "walk-in", 3);
        db.Weddings.Add(wedding);
        await db.SaveChangesAsync();
        var path = $"/api/weddings/{wedding.Id}/guest-planning";
        var import = new HttpRequestMessage(HttpMethod.Post, path + "/imports")
        { Content = JsonContent.Create(new PlanningImport([new(1, "Lê Văn Cường", "Anh", 2, "GROOM")])) };
        import.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, (await owner.SendAsync(import)).StatusCode);
        var cuong = await db.Set<PlanningGuest>().AsNoTracking().SingleAsync();
        var main = await db.Set<PlanningParticipant>().AsNoTracking().SingleAsync(x => x.GuestId == cuong.Id);
        using var anonymous = factory.CreateHttpsClient();
        Assert.Equal(HttpStatusCode.Created,
            (await anonymous.PostAsJsonAsync($"/api/rsvp/{cuong.InvitationToken}", new SubmitRsvp(0, "ATTENDING", [main.Id]))).StatusCode);
        // Capacity 3, 1 person attending.

        var stranger = new AddWalkIn(null, "Phạm Thị Dung", 2, "Chị", "BRIDE", ["Bé Na"]);
        await Problem(HttpStatusCode.BadRequest, "VALIDATION_FAILED", await owner.PostAsJsonAsync(path + "/walk-ins", stranger));
        var key = Guid.NewGuid().ToString();
        var dung = await Read<WalkInResultDto>(await Send(owner, path, key, stranger));
        Assert.Equal((true, false, "Phạm Thị Dung"), (dung.NewGuest, dung.OverCapacity, dung.RecipientName));
        Assert.Equal(new[] { "Phạm Thị Dung", "Bé Na" }, dung.Places.Select(x => x.PersonName));
        Assert.All(dung.Places, x => Assert.Equal((CheckInOutcome.CheckedIn, PlanningCheckInKind.WalkIn), (x.Result, x.Kind)));

        // A retry with the same key replays; the same key with another body is refused.
        var replay = await Read<WalkInResultDto>(await Send(owner, path, key, stranger));
        Assert.Equal(dung.GuestId, replay.GuestId);
        Assert.Equal(dung.Places.Select(x => x.PlaceId), replay.Places.Select(x => x.PlaceId));
        Assert.Equal(1, await db.Set<PlanningGuest>().CountAsync(x => x.RecipientName == "Phạm Thị Dung"));
        await Problem(HttpStatusCode.Conflict, "IDEMPOTENCY_KEY_REUSED", await Send(owner, path, key, stranger with { People = 1, Names = null }));

        // Capacity is full (3/3): one more person needs a reason, then is added to Cường's invitation at position 3.
        var extra = new AddWalkIn(cuong.Id, null, 1);
        await Problem(HttpStatusCode.UnprocessableEntity, "WALK_IN_OVER_CAPACITY", await Send(owner, path, Guid.NewGuid().ToString(), extra));
        var added = await Read<WalkInResultDto>(await Send(owner, path, Guid.NewGuid().ToString(), extra with { OverrideReason = "Con của anh Cường" }));
        var cuongExtra = Assert.Single(added.Places);
        Assert.Equal((false, true, 3, "Người đi cùng 3"), (added.NewGuest, added.OverCapacity, cuongExtra.Position, cuongExtra.PersonName));
        Assert.Equal("Con của anh Cường", (await db.Set<PlanningCheckIn>().SingleAsync(x => x.PlaceId == cuongExtra.PlaceId)).ExceptionReason);
        var lookup = await Read<SeatLookupDto>(await owner.GetAsync(path + $"/seat-lookup?code={cuong.InvitationToken}"));
        Assert.Equal(new[] { null, null, PlanningCheckInKind.WalkIn }, lookup.Seats.Select(x => x.CheckInKind));
        await Problem(HttpStatusCode.NotFound, "GUEST_NOT_FOUND", await Send(owner, path, Guid.NewGuid().ToString(), extra with { GuestId = Guid.NewGuid() }));

        var summary = await Read<CheckInSummaryDto>(await owner.GetAsync(path + "/check-ins/summary"));
        Assert.Equal((1, 3, 3, 0), (summary.ExpectedPlaces, summary.CheckedIn, summary.CheckedInWalkIns, summary.CheckedInConfirmed));

        // Undo: a reason is required; a walk-in person stops attending and frees the capacity; voiding twice is 404.
        var na = dung.Places[1].PlaceId;
        await Problem(HttpStatusCode.BadRequest, "VALIDATION_FAILED", await owner.PostAsJsonAsync(path + $"/check-ins/{na}/void", new VoidCheckIn(" ")));
        var voided = await Read<VoidCheckInResultDto>(await owner.PostAsJsonAsync(path + $"/check-ins/{na}/void", new VoidCheckIn("Bấm nhầm")));
        Assert.Equal((dung.GuestId, na), (voided.GuestId, voided.PlaceId));
        Assert.Null(voided.Seats.Single(x => x.PlaceId == na).CheckedInAt);
        Assert.Equal("DECLINED", (await db.Set<PlanningParticipant>().AsNoTracking().SingleAsync(x => x.FullName == "Bé Na")).AttendanceStatus);
        await Problem(HttpStatusCode.NotFound, "CHECK_IN_NOT_FOUND", await owner.PostAsJsonAsync(path + $"/check-ins/{na}/void", new VoidCheckIn("Lần hai")));

        // Voiding an invited check-in keeps the RSVP answer, so the guest can be checked in again.
        var first = cuong.Id;
        var cuongMain = lookup.Seats[0].PlaceId;
        await Read<CheckInResultDto>(await owner.PostAsJsonAsync(path + "/check-ins", new CheckInGuests(null, first, [cuongMain])));
        await Read<VoidCheckInResultDto>(await owner.PostAsJsonAsync(path + $"/check-ins/{cuongMain}/void", new VoidCheckIn("Nhầm người")));
        Assert.Equal("ATTENDING", (await db.Set<PlanningParticipant>().AsNoTracking().SingleAsync(x => x.Id == main.Id)).AttendanceStatus);
        Assert.Equal(CheckInOutcome.CheckedIn, Assert.Single((await Read<CheckInResultDto>(await owner.PostAsJsonAsync(path + "/check-ins",
            new CheckInGuests(null, first, [cuongMain])))).Places).Result);

        var audits = await db.AuditLogs.AsNoTracking().Where(x => x.WeddingId == wedding.Id).ToListAsync();
        Assert.Equal(2, audits.Count(x => x.Action == "GUEST_WALK_IN"));
        Assert.Equal(2, audits.Count(x => x.Action == "GUEST_CHECK_IN_VOIDED"));
        Assert.All(audits, x => Assert.DoesNotContain(cuong.InvitationToken!, x.Metadata ?? ""));

        using var outsider = await CoHostPermissionTests.SignIn(factory, "walkin-outsider");
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(outsider, path, Guid.NewGuid().ToString(), stranger)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync(path + $"/check-ins/{cuongMain}/void", new VoidCheckIn("x"))).StatusCode);
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ArchivedInvitationsTakeNoWalkInsAndFreeCapacity() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        await using var factory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
        using var owner = await CoHostPermissionTests.SignIn(factory, "walkin-archive-owner");
        var account = (await owner.GetFromJsonAsync<AccountDto>("/api/auth/me"))!;
        var wedding = Wedding.Create(account.Id, "Archived Walk-in Wedding", "walk-in-archived", 2);
        db.Weddings.Add(wedding);
        await db.SaveChangesAsync();
        var path = $"/api/weddings/{wedding.Id}/guest-planning";
        var import = new HttpRequestMessage(HttpMethod.Post, path + "/imports")
        { Content = JsonContent.Create(new PlanningImport([new(1, "Hồ Văn Tâm", "Anh", 1, "GROOM")])) };
        import.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, (await owner.SendAsync(import)).StatusCode);
        var tam = await db.Set<PlanningGuest>().AsNoTracking().SingleAsync();
        var main = await db.Set<PlanningParticipant>().AsNoTracking().SingleAsync(x => x.GuestId == tam.Id);
        using var anonymous = factory.CreateHttpsClient();
        Assert.Equal(HttpStatusCode.Created,
            (await anonymous.PostAsJsonAsync($"/api/rsvp/{tam.InvitationToken}", new SubmitRsvp(0, "ATTENDING", [main.Id]))).StatusCode);
        var version = (await db.Set<PlanningGuest>().AsNoTracking().SingleAsync(x => x.Id == tam.Id)).Version;
        Assert.True((await owner.DeleteAsync(path + $"/guests/{tam.Id}?expectedVersion={version}")).IsSuccessStatusCode);

        // The archived invitation takes no extra people, like check-in by guest id, and its attending person no longer
        // uses the capacity (same rule as RSVP): two walk-ins fit in a capacity of 2 without a reason.
        await Problem(HttpStatusCode.NotFound, "GUEST_NOT_FOUND", await Send(owner, path, Guid.NewGuid().ToString(), new AddWalkIn(tam.Id, null, 1)));
        Assert.Equal(1, await db.Set<PlanningPlace>().CountAsync(x => x.GuestId == tam.Id));
        var walkIn = await Read<WalkInResultDto>(await Send(owner, path, Guid.NewGuid().ToString(), new AddWalkIn(null, "Ngô Thị Hoa", 2)));
        Assert.Equal((true, false, 2), (walkIn.NewGuest, walkIn.OverCapacity, walkIn.Places.Count));
    });

    private static Task<HttpResponseMessage> Send(HttpClient client, string path, string key, AddWalkIn input)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path + "/walk-ins") { Content = JsonContent.Create(input) };
        request.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(request);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task Problem(HttpStatusCode status, string code, HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}");
        Assert.Equal(code, JsonDocument.Parse(body).RootElement.GetProperty("code").GetString());
    }
}
