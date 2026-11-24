using System.Net;
using System.Net.Http.Json;
using InviteMe.Application.Features.Seating.Planning;
using InviteMe.Domain.Seating;

namespace InviteMe.IntegrationTests;

public sealed class SeatingCanvasShapeTests
{
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
