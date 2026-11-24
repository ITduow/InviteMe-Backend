using System.Net;
using System.Net.Http.Json;
using InviteMe.Application.Features.Seating.Planning;
using InviteMe.Domain.Seating;

namespace InviteMe.IntegrationTests;

public sealed class SeatingCanvasShapeTests
{
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ExplicitTableSideChangesPreserveSeatsAndCanvasCannotChangeSide() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "table-side-owner");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "table-side");
        var root = $"/api/weddings/{wedding.Id}/guest-planning";
        using var import = new HttpRequestMessage(HttpMethod.Post, root + "/imports")
        {
            Content = JsonContent.Create(new InviteMe.Application.Features.Guests.Planning.PlanningImport([new(2, "Party", ExpectedPeople: 2)]))
        };
        import.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, (await owner.SendCheckedImportAsync(import)).StatusCode);
        var url = root + "/seating-plan";
        var plan = (await owner.GetFromJsonAsync<PlanningSeatingDto>(url))!;
        async Task Change(ChangePlanningSeating input)
        {
            var response = await owner.PatchAsJsonAsync(url, input);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            plan = (await response.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        }
        await Change(new(plan.Version, "CREATE_TABLE", Name: "Neutral", Capacity: 10));
        var table = Assert.Single(plan.Tables);
        Assert.Null(table.Side);
        await Change(new(plan.Version, "MOVE_PLACES", table.Id, PlaceIds: plan.Places.Select(p => p.Id).ToArray()));
        var seats = plan.Assignments.ToArray();
        await Change(new(plan.Version, "UPDATE_TABLE", table.Id, Name: table.Name, Capacity: 10, Side: "GROOM"));
        Assert.Equal("GROOM", Assert.Single(plan.Tables).Side);
        // Legacy zone/side metadata must never change a table's explicit side.
        var canvas = new CanvasLayout(true, new() { [table.Id.ToString()] = new(1300, -250, Side: "BRIDE") },
            new() { ["BRIDE"] = new(0, 0, 100, 100) }, null, []);
        await Change(new(plan.Version, "UPDATE_CANVAS_LAYOUT", CanvasLayout: canvas));
        Assert.Equal("GROOM", Assert.Single(plan.Tables).Side);
        Assert.False(plan.CanvasLayout!.SplitZones);
        Assert.Empty(plan.CanvasLayout.Zones);
        Assert.Null(plan.CanvasLayout.Tables[table.Id.ToString()].Side);
        foreach (var side in new[] { "BRIDE", "GROOM", "UNSPECIFIED" })
        {
            await Change(new(plan.Version, "UPDATE_TABLE", table.Id, Name: table.Name, Capacity: 10, Side: side));
            Assert.Equal(side == "UNSPECIFIED" ? null : side, Assert.Single(plan.Tables).Side);
            Assert.Equal(seats.OrderBy(a => a.PlaceId), plan.Assignments.OrderBy(a => a.PlaceId));
            var position = plan.CanvasLayout!.Tables[table.Id.ToString()];
            Assert.Equal((1300d, -250d), (position.X, position.Y));
        }
        var reopened = (await owner.GetFromJsonAsync<PlanningSeatingDto>(url))!;
        Assert.Null(Assert.Single(reopened.Tables).Side);
        Assert.Equal(seats.OrderBy(a => a.PlaceId), reopened.Assignments.OrderBy(a => a.PlaceId));
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task StageSizesRoundTripAndInvalidSizesDoNotChangeThePlan() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "canvas-stage-owner");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "canvas-stage");
        var url = $"/api/weddings/{wedding.Id}/guest-planning/seating-plan";
        var plan = (await owner.GetFromJsonAsync<PlanningSeatingDto>(url))!;
        foreach (var width in new double?[] { null, 500 })
        {
            var canvas = new CanvasLayout(false, new(), new(), new(10, 20, width, width is null ? null : 320),
                [new("stage-1", "stage", 50, 60, width, width is null ? null : 320)]);
            var response = await owner.PatchAsJsonAsync(url, new ChangePlanningSeating(plan.Version, "UPDATE_CANVAS_LAYOUT", CanvasLayout: canvas));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            plan = (await owner.GetFromJsonAsync<PlanningSeatingDto>(url))!;
            Assert.Equal(width, plan.CanvasLayout!.Stage!.Width);
            Assert.Equal(width, Assert.Single(plan.CanvasLayout.Objects).Width);
        }
        foreach (var size in new (double? Width, double? Height)[] { (0, 320), (-1, 320), (500, null) })
        {
            foreach (var legacyStage in new[] { true, false })
            {
                var invalid = plan.CanvasLayout! with {
                    Stage = legacyStage ? new(10, 20, size.Width, size.Height) : null,
                    Objects = legacyStage ? [] : [new("stage-1", "stage", 10, 20, size.Width, size.Height)]
                };
                var response = await owner.PatchAsJsonAsync(url, new ChangePlanningSeating(plan.Version, "UPDATE_CANVAS_LAYOUT", CanvasLayout: invalid));
                Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
                Assert.Equal(plan.Version, (await owner.GetFromJsonAsync<PlanningSeatingDto>(url))!.Version);
            }
        }
    });

    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task ShapesRoundTripAndInvalidShapeDoesNotChangeThePlan() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        using var owner = await CoHostPermissionTests.SignIn(factory, "canvas-shape-owner");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "canvas-shapes");
        var url = $"/api/weddings/{wedding.Id}/guest-planning/seating-plan";
        var response = await owner.PatchAsJsonAsync(url, new ChangePlanningSeating(0, "CREATE_TABLE", Name: "Shape table", Capacity: 10));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var plan = (await response.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        var table = Assert.Single(plan.Tables);
        foreach (var shape in new string?[] { null, "twoSided", "fourSided", "round" })
        {
            var canvas = new CanvasLayout(false, new() { [table.Id.ToString()] = new(100, 200, Shape: shape) }, new(), null, [new("stage-1", "stage", 50, 20)]);
            response = await owner.PatchAsJsonAsync(url, new ChangePlanningSeating(plan.Version, "UPDATE_CANVAS_LAYOUT", CanvasLayout: canvas));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            plan = (await owner.GetFromJsonAsync<PlanningSeatingDto>(url))!;
            Assert.Equal(shape, plan.CanvasLayout!.Tables[table.Id.ToString()].Shape);
            Assert.Equal(table, Assert.Single(plan.Tables));
            Assert.Empty(plan.Assignments);
        }
        var invalid = plan.CanvasLayout! with { Tables = new() { [table.Id.ToString()] = new(100, 200, Shape: "invalid") } };
        response = await owner.PatchAsJsonAsync(url, new ChangePlanningSeating(plan.Version, "UPDATE_CANVAS_LAYOUT", CanvasLayout: invalid));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(plan.Version, (await owner.GetFromJsonAsync<PlanningSeatingDto>(url))!.Version);
    });
}
