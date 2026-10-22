using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InviteMe.Application.Features.Guests.Access;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Guests.Reception;
using InviteMe.Application.Features.Rsvps;
using InviteMe.Application.Features.Seating.Planning;
using InviteMe.Application.Features.Weddings.CoHosts;
using InviteMe.Domain.CheckIn;
using InviteMe.Domain.Guests;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.IntegrationTests;

// Cohost04 (reception check-in) and the reception part of Cohost05 on planning-v2 invitations, through real HTTP.
public sealed class ReceptionCheckInApiTests
{
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task CheckInCountsEachPlaceOnceAndFollowsRsvpStatusAndPermissions() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        await using var factory = new DatabaseApiFactory(db.Database.GetConnectionString()!);
        using var owner = await CoHostPermissionTests.SignIn(factory, "reception-owner");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "reception");
        var path = $"/api/weddings/{wedding.Id}/guest-planning";
        var import = new HttpRequestMessage(HttpMethod.Post, path + "/imports")
        {
            Content = JsonContent.Create(new PlanningImport([
                new(2, "Nguyễn Văn Đức", "Bác", "FAMILY", 3, "BRIDE", Phone: "0901 234 567"),
                new(3, "Trần Thị Bình", "Chị", "INDIVIDUAL", 1, "GROOM")]))
        };
        import.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, (await owner.SendAsync(import)).StatusCode);
        var guests = await db.Set<PlanningGuest>().AsNoTracking().OrderBy(x => x.RowNumber).ToListAsync();
        var (duc, binh) = (guests[0], guests[1]);
        using var anonymous = factory.CreateHttpsClient();

        // Đức answers for himself only: places 2-3 become UNUSED. Bình never answers (PENDING). Đức sits at "Bàn 1".
        var main = await db.Set<PlanningParticipant>().AsNoTracking().SingleAsync(x => x.GuestId == duc.Id);
        Assert.Equal(HttpStatusCode.Created,
            (await anonymous.PostAsJsonAsync($"/api/rsvp/{duc.InvitationToken}", new SubmitRsvp(0, "ATTENDING", [main.Id]))).StatusCode);
        var plan = await Read<PlanningSeatingDto>(await owner.PatchAsJsonAsync(path + "/seating-plan",
            new ChangePlanningSeating(0, "CREATE_TABLE", Name: "Bàn 1", Capacity: 10)));
        var ducPlaces = plan.Places.Where(x => x.GuestId == duc.Id).OrderBy(x => x.Position).Select(x => x.Id).ToList();
        await Read<PlanningSeatingDto>(await owner.PatchAsJsonAsync(path + "/seating-plan",
            new ChangePlanningSeating(plan.Version, "MOVE_PLACES", plan.Tables.Single().Id, PlaceIds: [ducPlaces[0]])));

        // Lookup is read only and shows the place ids to confirm.
        var link = $"http://localhost:3000/invite/{duc.InvitationToken}";
        var lookup = await Read<SeatLookupDto>(await owner.GetAsync(path + $"/seat-lookup?code={Uri.EscapeDataString(link)}"));
        Assert.Equal(ducPlaces, lookup.Seats.Select(x => x.PlaceId));
        Assert.Equal(new[] { RsvpPlaceStatus.Used, RsvpPlaceStatus.Unused, RsvpPlaceStatus.Unused }, lookup.Seats.Select(x => x.Status));
        Assert.All(lookup.Seats, x => Assert.Null(x.CheckedInAt));
        Assert.Equal(0, await db.Set<PlanningCheckIn>().CountAsync());

        // Scan and confirm the representative; a repeated scan counts nothing twice.
        var first = await Read<CheckInResultDto>(await owner.PostAsJsonAsync(path + "/check-ins", new CheckInGuests(link, null, [ducPlaces[0]])));
        var seat = Assert.Single(first.Places);
        Assert.Equal(("Nguyễn Văn Đức", "Bàn 1", CheckInOutcome.CheckedIn), (seat.PersonName, seat.Table, seat.Result));
        var again = Assert.Single((await Read<CheckInResultDto>(await owner.PostAsJsonAsync(path + "/check-ins",
            new CheckInGuests(duc.InvitationToken, null, [ducPlaces[0]])))).Places);
        Assert.Equal((CheckInOutcome.AlreadyCheckedIn, seat.CheckedInAt), (again.Result, again.CheckedInAt));
        Assert.Equal(1, await db.Set<PlanningCheckIn>().CountAsync());

        // A place the guest declined needs a reason; a place of another invitation is refused.
        await Problem(HttpStatusCode.UnprocessableEntity, "CHECKIN_EXCEPTION_REASON_REQUIRED",
            await owner.PostAsJsonAsync(path + "/check-ins", new CheckInGuests(link, null, [ducPlaces[1]])));
        var binhPlace = plan.Places.Single(x => x.GuestId == binh.Id).Id;
        await Problem(HttpStatusCode.UnprocessableEntity, "PLACE_NOT_IN_INVITATION",
            await owner.PostAsJsonAsync(path + "/check-ins", new CheckInGuests(link, null, [binhPlace])));
        await Problem(HttpStatusCode.BadRequest, "VALIDATION_FAILED",
            await owner.PostAsJsonAsync(path + "/check-ins", new CheckInGuests(link, binh.Id, [binhPlace])));
        var exception = Assert.Single((await Read<CheckInResultDto>(await owner.PostAsJsonAsync(path + "/check-ins",
            new CheckInGuests(link, null, [ducPlaces[1]], "Đổi ý, đến cùng gia đình")))).Places);
        Assert.Equal((RsvpPlaceStatus.Unused, CheckInOutcome.CheckedIn), (exception.Status, exception.Result));
        Assert.Equal("Đổi ý, đến cùng gia đình", (await db.Set<PlanningCheckIn>().SingleAsync(x => x.PlaceId == ducPlaces[1])).ExceptionReason);

        // Two counters confirm the same place at once: one row, one audit entry.
        var racing = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => owner.PostAsJsonAsync(path + "/check-ins",
            new CheckInGuests(duc.InvitationToken, null, [ducPlaces[2]], "Đến muộn"))));
        var outcomes = await Task.WhenAll(racing.Select(async r => Assert.Single((await Read<CheckInResultDto>(r)).Places).Result));
        Assert.Equal(new[] { CheckInOutcome.AlreadyCheckedIn, CheckInOutcome.CheckedIn }, outcomes.Order());
        Assert.Equal(1, await db.Set<PlanningCheckIn>().CountAsync(x => x.PlaceId == ducPlaces[2]));
        var audits = await db.AuditLogs.AsNoTracking().Where(x => x.EntityType == "planning_check_ins").ToListAsync();
        Assert.Equal(3, audits.Count);
        Assert.All(audits, x => Assert.DoesNotContain(duc.InvitationToken!, x.Metadata ?? ""));

        // Guest without QR: search by name without accents or by phone, then check in by guest id (no RSVP yet).
        var byName = await Read<List<ReceptionGuestDto>>(await owner.GetAsync(path + "/reception/search?q=nguyen van duc"));
        Assert.Equal((duc.Id, 3, 3, "Bàn 1"), (Assert.Single(byName).GuestId, byName[0].Places, byName[0].CheckedIn, Assert.Single(byName[0].Tables)));
        var byPhone = await owner.GetAsync(path + "/reception/search?q=234567");
        Assert.Equal(duc.Id, Assert.Single((await Read<List<ReceptionGuestDto>>(byPhone))).GuestId);
        Assert.DoesNotContain("0901", await byPhone.Content.ReadAsStringAsync());
        Assert.Equal(binh.Id, Assert.Single(await Read<List<ReceptionGuestDto>>(await owner.GetAsync(path + "/reception/search?q=BÌNH"))).GuestId);
        await Problem(HttpStatusCode.BadRequest, "VALIDATION_FAILED", await owner.GetAsync(path + "/reception/search?q=a"));
        var walkUp = Assert.Single((await Read<CheckInResultDto>(await owner.PostAsJsonAsync(path + "/check-ins",
            new CheckInGuests(null, binh.Id, [binhPlace])))).Places);
        Assert.Equal((RsvpPlaceStatus.Pending, CheckInOutcome.CheckedIn, (string?)null), (walkUp.Status, walkUp.Result, walkUp.Table));

        var after = await Read<SeatLookupDto>(await owner.GetAsync(path + $"/seat-lookup?code={duc.InvitationToken}"));
        Assert.All(after.Seats, x => Assert.NotNull(x.CheckedInAt));

        // Summary: 1 confirmed place expected; 4 arrived = 1 confirmed + 1 without RSVP + 2 exceptions.
        var summary = await Read<CheckInSummaryDto>(await owner.GetAsync(path + "/check-ins/summary"));
        Assert.Equal((1, 4, 1, 1, 2), (summary.ExpectedPlaces, summary.CheckedIn, summary.CheckedInConfirmed, summary.CheckedInWithoutRsvp,
            summary.CheckedInExceptions));
        Assert.Equal(new[] { ("BRIDE", 1, 3), ("GROOM", 0, 1) }, summary.BySide.Select(x => (x.Side, x.ExpectedPlaces, x.CheckedIn)));

        // Permissions: CHECKIN_MANAGE works wedding-wide; guest management alone, strangers and anonymous callers do not.
        using var staff = await CoHostPermissionTests.SignIn(factory, "reception-staff");
        using var planner = await CoHostPermissionTests.SignIn(factory, "reception-planner");
        using var outsider = await CoHostPermissionTests.SignIn(factory, "reception-outsider");

        await CoHostPermissionTests.AddCoHostAsync(owner, staff, wedding.Id, "CHECKIN_MANAGE");
        await CoHostPermissionTests.AddCoHostAsync(owner, planner, wedding.Id, "GUEST_VIEW", "GUEST_EDIT");


        Assert.Equal(CheckInOutcome.AlreadyCheckedIn, Assert.Single((await Read<CheckInResultDto>(await staff.PostAsJsonAsync(path + "/check-ins",
            new CheckInGuests(null, binh.Id, [binhPlace])))).Places).Result);
        Assert.Equal(4, (await Read<CheckInSummaryDto>(await staff.GetAsync(path + "/check-ins/summary"))).CheckedIn);
        foreach (var client in new[] { planner, outsider })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path + "/check-ins", new CheckInGuests(null, binh.Id, [binhPlace]))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path + "/reception/search?q=binh")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path + "/check-ins/summary")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(path + "/check-ins", new CheckInGuests(null, binh.Id, [binhPlace]))).StatusCode);

        // A token or guest of another wedding is unknown here.
        var other = await CoHostPermissionTests.CreateWedding(db, owner, "reception-other");
        var otherPath = $"/api/weddings/{other.Id}/guest-planning";
        await Problem(HttpStatusCode.NotFound, "INVITATION_UNAVAILABLE",
            await owner.PostAsJsonAsync(otherPath + "/check-ins", new CheckInGuests(duc.InvitationToken, null, [ducPlaces[0]])));
        await Problem(HttpStatusCode.NotFound, "GUEST_NOT_FOUND",
            await owner.PostAsJsonAsync(otherPath + "/check-ins", new CheckInGuests(null, binh.Id, [binhPlace])));
    });

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
