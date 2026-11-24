using System.Net;
using System.Net.Http.Json;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Seating.Planning;

namespace InviteMe.IntegrationTests;

public sealed class SeatingPartyAnchorTests
{
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task PartyFollowsAnchorSkipsOccupiedSeatsAndWrapsWithoutPartialWrites() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "party-anchor-owner");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "party-anchor");
        var root = $"/api/weddings/{wedding.Id}/guest-planning";
        using var import = new HttpRequestMessage(HttpMethod.Post, root + "/imports")
        {
            Content = JsonContent.Create(new PlanningImport([new(2, "Party", ExpectedPeople: 2), new(3, "Other", ExpectedPeople: 1)]))
        };
        import.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, (await owner.SendCheckedImportAsync(import)).StatusCode);
        var url = root + "/seating-plan";
        var plan = (await owner.GetFromJsonAsync<PlanningSeatingDto>(url))!;
        async Task Change(ChangePlanningSeating change)
        {
            var response = await owner.PatchAsJsonAsync(url, change);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            plan = (await response.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        }
        await Change(new(plan.Version, "CREATE_TABLE", Name: "Table", Capacity: 10));
        var table = Assert.Single(plan.Tables).Id;
        var guest = plan.Guests.Single(g => g.RecipientName == "Party").Id;
        var party = plan.Places.Where(p => p.GuestId == guest).OrderBy(p => p.Position).Select(p => p.Id).ToArray();
        var other = plan.Places.Single(p => p.GuestId != guest).Id;
        await Change(new(plan.Version, "MOVE_PLACES", table, PlaceIds: [other], SeatNumber: 5));
        foreach (var (anchor, expected) in new[] { (4, new[] { 4, 6 }), (7, new[] { 7, 8 }), (10, new[] { 10, 1 }) })
        {
            await Change(new(plan.Version, "MOVE_PLACES", table, PlaceIds: party, SeatNumber: anchor));
            Assert.Equal(expected, party.Select(id => plan.Assignments.Single(a => a.PlaceId == id).SeatNumber).ToArray());
            Assert.Equal(5, plan.Assignments.Single(a => a.PlaceId == other).SeatNumber);
            var reopened = (await owner.GetFromJsonAsync<PlanningSeatingDto>(url))!;
            Assert.Equal(expected, party.Select(id => reopened.Assignments.Single(a => a.PlaceId == id).SeatNumber).ToArray());
        }
        var before = plan;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PatchAsJsonAsync(url,
            new ChangePlanningSeating(plan.Version, "MOVE_PLACES", table, PlaceIds: party, SeatNumber: 5))).StatusCode);
        plan = (await owner.GetFromJsonAsync<PlanningSeatingDto>(url))!;
        Assert.Equal(before.Version, plan.Version);
        Assert.Equal(before.Assignments.OrderBy(a => a.PlaceId), plan.Assignments.OrderBy(a => a.PlaceId));
        await Change(new(plan.Version, "CREATE_TABLE", Name: "Small", Capacity: 2));
        var small = plan.Tables.Single(t => t.Name == "Small").Id;
        await Change(new(plan.Version, "MOVE_PLACES", small, PlaceIds: [other]));
        before = plan;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await owner.PatchAsJsonAsync(url,
            new ChangePlanningSeating(plan.Version, "MOVE_PLACES", small, PlaceIds: party, SeatNumber: 2))).StatusCode);
        plan = (await owner.GetFromJsonAsync<PlanningSeatingDto>(url))!;
        Assert.Equal(before.Version, plan.Version);
        Assert.Equal(before.Assignments.OrderBy(a => a.PlaceId), plan.Assignments.OrderBy(a => a.PlaceId));
    });
}
