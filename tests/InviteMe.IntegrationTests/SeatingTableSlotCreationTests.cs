using System.Net;
using System.Net.Http.Json;
using InviteMe.Application.Features.Seating.Planning;

namespace InviteMe.IntegrationTests;

public sealed class SeatingTableSlotCreationTests
{
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task CreationFillsSlotsWithoutMovingExistingTablesAndRetiresOldSuggestions() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "slot-owner");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "slot-creation");
        var url = $"/api/weddings/{wedding.Id}/guest-planning/seating-plan";
        var response = await owner.PatchAsJsonAsync(url, new ChangePlanningSeating(0, "CREATE_TABLE", Name: "First", Capacity: 10, Side: "GROOM", Row: 1, Column: 2));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var first = (await response.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        var original = Assert.Single(first.Tables);
        Assert.Equal(("GROOM", 1, 2), (original.Side, original.Row, original.Column));

        response = await owner.PatchAsJsonAsync(url, new ChangePlanningSeating(first.Version, "CREATE_TABLES", Capacity: 10, TableCount: 2, Side: "GROOM"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var plan = (await response.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        Assert.Equal(3, plan.Tables.Count);
        Assert.Equal(original, plan.Tables.Single(t => t.Id == original.Id));
        Assert.Contains(plan.Tables, t => t.Side == "GROOM" && t.Row == 1 && t.Column == 1);
        Assert.Contains(plan.Tables, t => t.Side == "GROOM" && t.Row == 2 && t.Column == 1);

        response = await owner.PatchAsJsonAsync(url, new ChangePlanningSeating(plan.Version, "CREATE_TABLE", Name: "Collision", Capacity: 10, Side: "GROOM", Row: 1, Column: 2));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        response = await owner.PatchAsJsonAsync(url, new ChangePlanningSeating(plan.Version, "CREATE_TABLE", Name: "Outside", Capacity: 10, Side: "GROOM", Row: 1, Column: 3));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var unchanged = (await owner.GetFromJsonAsync<PlanningSeatingDto>(url))!;
        Assert.Equal(plan.Version, unchanged.Version);
        Assert.Equal(3, unchanged.Tables.Count);
        Assert.Empty(unchanged.Assignments);

        response = await owner.PatchAsJsonAsync(url, new ChangePlanningSeating(plan.Version, "CREATE_TABLE", Name: "Bride", Capacity: 10, Side: "BRIDE", Row: 1, Column: 2));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PatchAsJsonAsync(url, new { expectedVersion = plan.Version + 1, action = "APPLY_SUGGESTION" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.PostAsJsonAsync($"/api/weddings/{wedding.Id}/guest-planning/seating-suggestions", new { strategy = "AFFINITY" })).StatusCode);
    });
}
