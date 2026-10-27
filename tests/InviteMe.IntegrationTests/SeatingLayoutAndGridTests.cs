using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Identity.Accounts;
using InviteMe.Application.Features.Seating.Planning;
using InviteMe.Application.Ports.Authentication;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.IntegrationTests;

public sealed class SeatingLayoutAndGridTests
{
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task LayoutAndTableGridLifecycleOperatesCorrectly() => SchemaV2MigrationTests.InDatabase(async db =>
    {
        await db.Database.MigrateAsync();
        await using var factory = new DatabaseApiFactory(db.Database.GetConnectionString()!);

        async Task<HttpClient> Account(string name)
        {
            var client = factory.CreateHttpsClient();
            const string password = "Test-only-Pass123!";
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/auth/register", new RegisterAccount(name + "@example.test", password, name))).StatusCode);
            var login = await client.PostAsJsonAsync("/api/auth/login", new LoginAccount(name + "@example.test", password));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<TokenDto>())!.AccessToken);
            return client;
        }

        using var owner = await Account("grid-owner");
        using var cohost = await Account("grid-cohost");
        var wedding = await CoHostPermissionTests.CreateWedding(db, owner, "grid-test");
        await CoHostPermissionTests.AddCoHostAsync(owner, cohost, wedding.Id, "SEATING_VIEW", "SEATING_EDIT");

        var root = $"/api/weddings/{wedding.Id}/guest-planning";
        var layoutUrl = $"/api/weddings/{wedding.Id}/seating-layout";
        var planUrl = root + "/seating-plan";

        // 1. GET Layout returns default 6x3
        var defaultLayout = await owner.GetFromJsonAsync<SeatingLayoutDto>(layoutUrl);
        Assert.NotNull(defaultLayout);
        Assert.Equal(6, defaultLayout.GroomRows);
        Assert.Equal(3, defaultLayout.GroomColumns);
        Assert.Equal(6, defaultLayout.BrideRows);
        Assert.Equal(3, defaultLayout.BrideColumns);
        Assert.Equal("BY_ROW", defaultLayout.Numbering);

        // 2. PUT Layout updates layout
        var updateLayoutRes = await owner.PutAsJsonAsync(layoutUrl, new UpdateSeatingLayout(8, 4, 8, 4, "ODD_EVEN", defaultLayout.Version));
        Assert.Equal(HttpStatusCode.OK, updateLayoutRes.StatusCode);
        var updatedLayout = await updateLayoutRes.Content.ReadFromJsonAsync<SeatingLayoutDto>();
        Assert.NotNull(updatedLayout);
        Assert.Equal(8, updatedLayout.GroomRows);
        Assert.Equal(4, updatedLayout.GroomColumns);
        Assert.Equal(8, updatedLayout.BrideRows);
        Assert.Equal(4, updatedLayout.BrideColumns);
        Assert.Equal("ODD_EVEN", updatedLayout.Numbering);
        Assert.Equal(1, updatedLayout.Version);

        // 3. CREATE_TABLE validates capacity (only 10 or 12 allowed)
        var invalidCap = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(1, "CREATE_TABLE", Name: "Bàn 5", Capacity: 5));
        Assert.Equal(HttpStatusCode.BadRequest, invalidCap.StatusCode);

        // 4. CREATE_TABLE with coordinates (placed directly on grid: Groom row 1, col 1)
        var createPlacedRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(1, "CREATE_TABLE", Name: "Bàn 01", Capacity: 10, Side: "GROOM", Row: 1, Column: 1));
        Assert.Equal(HttpStatusCode.OK, createPlacedRes.StatusCode);
        var plan = (await createPlacedRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        Assert.Equal(2, plan.Version);
        var t1 = Assert.Single(plan.Tables);
        Assert.Equal(1, t1.Number);
        Assert.Equal("GROOM", t1.Side);
        Assert.Equal(1, t1.Row);
        Assert.Equal(1, t1.Column);

        // 5. CREATE_TABLE outside layout bounds is rejected (layout is 8 rows x 4 columns)
        var outOfBounds = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "CREATE_TABLE", Capacity: 10, Side: "GROOM", Row: 9, Column: 1));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, outOfBounds.StatusCode);

        // 6. CREATE_TABLE on already occupied cell is rejected
        var occupied = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "CREATE_TABLE", Capacity: 10, Side: "GROOM", Row: 1, Column: 1));
        Assert.Equal(HttpStatusCode.Conflict, occupied.StatusCode);

        // 7. CREATE_TABLES creates batch in tray with sequential numbers
        var batchRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "CREATE_TABLES", Capacity: 10, TableCount: 3));
        Assert.Equal(HttpStatusCode.OK, batchRes.StatusCode);
        plan = (await batchRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        Assert.Equal(4, plan.Tables.Count);
        Assert.Equal(2, plan.Tables[1].Number);
        Assert.Equal(3, plan.Tables[2].Number);
        Assert.Equal(4, plan.Tables[3].Number);
        Assert.Null(plan.Tables[1].Row); // in tray

        // 8. PLACE_TABLE: Place Table 2 on Bride side row 1, col 1
        var placeT2 = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "PLACE_TABLE", plan.Tables[1].Id, Side: "BRIDE", Row: 1, Column: 1));
        Assert.Equal(HttpStatusCode.OK, placeT2.StatusCode);
        plan = (await placeT2.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        var t2 = plan.Tables.Single(t => t.Number == 2);
        Assert.Equal("BRIDE", t2.Side);
        Assert.Equal(1, t2.Row);
        Assert.Equal(1, t2.Column);

        // 9. SWAP_TABLES: Swap Table 1 (Groom 1,1) and Table 2 (Bride 1,1)
        var swapRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "SWAP_TABLES", t1.Id, TargetTableId: t2.Id));
        Assert.Equal(HttpStatusCode.OK, swapRes.StatusCode);
        plan = (await swapRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        t1 = plan.Tables.Single(t => t.Id == t1.Id);
        t2 = plan.Tables.Single(t => t.Id == t2.Id);
        Assert.Equal(1, t1.Row); Assert.Equal(1, t1.Column); Assert.Equal("BRIDE", t1.Side);
        Assert.Equal(1, t2.Row); Assert.Equal(1, t2.Column); Assert.Equal("GROOM", t2.Side);

        // 10. TAG_TABLES: tag Table 3 and Table 4 with IsVip = true
        var tagRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "TAG_TABLES", TableIds: [plan.Tables[2].Id, plan.Tables[3].Id], IsVip: true));
        Assert.Equal(HttpStatusCode.OK, tagRes.StatusCode);
        plan = (await tagRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        Assert.True(plan.Tables.Single(t => t.Id == plan.Tables[2].Id).IsVip);
        Assert.True(plan.Tables.Single(t => t.Id == plan.Tables[3].Id).IsVip);

        // 11. RENUMBER_TABLES: Renumber by ODD_EVEN
        var renumberRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "RENUMBER_TABLES"));
        Assert.Equal(HttpStatusCode.OK, renumberRes.StatusCode);
        plan = (await renumberRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        // Placed on Groom (t2) gets odd (1), placed on Bride (t1) gets even (2), tray tables get next numbers (3, 4)
        var newT2 = plan.Tables.Single(t => t.Id == t2.Id);
        var newT1 = plan.Tables.Single(t => t.Id == t1.Id);
        Assert.Equal(1, newT2.Number);
        Assert.Equal(2, newT1.Number);

        // 12. Move table to tray (clears Row, Column, Side)
        var toTrayRes = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "MOVE_TABLE", newT1.Id, Side: "TRAY"));
        Assert.Equal(HttpStatusCode.OK, toTrayRes.StatusCode);
        plan = (await toTrayRes.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        Assert.Null(plan.Tables.Single(t => t.Id == newT1.Id).Row);

        // 13. Reducing layout when table is outside bounds fails with LAYOUT_HAS_TABLES_OUTSIDE
        // Place t2 at Groom Row 8, Col 4
        var placeDeep = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "PLACE_TABLE", newT2.Id, Side: "GROOM", Row: 8, Column: 4));
        Assert.Equal(HttpStatusCode.OK, placeDeep.StatusCode);
        plan = (await placeDeep.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;

        var shrinkConflict = await owner.PutAsJsonAsync(layoutUrl, new UpdateSeatingLayout(5, 3, 5, 3, "BY_ROW", plan.Version));
        Assert.Equal(HttpStatusCode.Conflict, shrinkConflict.StatusCode);

        // 14. Import guest, seat on table, and delete table requires confirm
        using var import = new HttpRequestMessage(HttpMethod.Post, root + "/imports")
        {
            Content = JsonContent.Create(new PlanningImport([new(2, "Khách test", ExpectedPeople: 2, Side: "GROOM")]))
        };
        import.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Created, (await owner.SendAsync(import)).StatusCode);
        plan = (await owner.GetFromJsonAsync<PlanningSeatingDto>(planUrl))!;
        var guestPlaces = plan.Places.Select(p => p.Id).ToArray();
        plan = (await (await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "MOVE_PLACES", newT2.Id, PlaceIds: guestPlaces))).Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        Assert.Equal(2, plan.Assignments.Count);

        // Delete without confirmation fails
        var deleteFail = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "DELETE_TABLE", newT2.Id, ConfirmDeleteWithGuests: false));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, deleteFail.StatusCode);

        // Delete with confirmation succeeds, guest place assignments unseated back to tray
        var deleteOk = await owner.PatchAsJsonAsync(planUrl, new ChangePlanningSeating(plan.Version, "DELETE_TABLE", newT2.Id, ConfirmDeleteWithGuests: true));
        Assert.Equal(HttpStatusCode.OK, deleteOk.StatusCode);
        plan = (await deleteOk.Content.ReadFromJsonAsync<PlanningSeatingDto>())!;
        Assert.Empty(plan.Assignments);
        Assert.DoesNotContain(plan.Tables, t => t.Id == newT2.Id);
    });
}
